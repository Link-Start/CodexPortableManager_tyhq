using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace CodexPortableManager
{
    internal sealed class RemoteRangeReader
    {
        internal const int MaximumSingleRangeLength = 16 * 1024 * 1024;
        private readonly ArtifactPipeline pipeline;
        private readonly string url;
        private readonly long packageLength;
        private readonly OperationPauseToken pauseToken;
        private readonly IProgress<OperationProgress> progress;
        private readonly RemoteRangeCache cache = new RemoteRangeCache();
        private string strongEntityTag;
        private int requestCount;
        private long networkBytesRead;
        private long completedTargetBytes;
        private long targetBytes;
        private long reusedBytes;
        private readonly Stopwatch transferClock = Stopwatch.StartNew();
        private long speedSampleNetworkBytes;
        private TimeSpan speedSampleTime;
        private TimeSpan lastProgressReportTime;
        private double smoothedBytesPerSecond;
        private int lastTransferPercent = -1;

        internal RemoteRangeReader(
            ArtifactPipeline pipeline,
            string url,
            long packageLength,
            OperationPauseToken pauseToken)
            : this(pipeline, url, packageLength, pauseToken, null)
        {
        }

        internal RemoteRangeReader(
            ArtifactPipeline pipeline,
            string url,
            long packageLength,
            OperationPauseToken pauseToken,
            IProgress<OperationProgress> progressValue)
        {
            this.pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
            if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("远程 MSIX 地址不能为空。", nameof(url));
            if (packageLength <= 0) throw new ArgumentOutOfRangeException(nameof(packageLength));
            this.url = url;
            this.packageLength = packageLength;
            this.pauseToken = pauseToken ?? new OperationPauseToken(null);
            progress = progressValue;
        }

        internal long PackageLength { get { return packageLength; } }
        internal int RequestCount { get { return requestCount; } }
        internal long NetworkBytesRead { get { return networkBytesRead; } }

        internal void UpdateMaterializationProgress(long completed, long target, long reused)
        {
            bool starting = targetBytes <= 0 && target > 0;
            completedTargetBytes = Math.Max(0, completed);
            targetBytes = Math.Max(0, target);
            reusedBytes = Math.Max(0, reused);
            if (starting) ResetSpeedSample();
        }

        internal void ReportMaterializationProgress(
            long completed,
            long target,
            long reused,
            IProgress<OperationProgress> fallbackProgress)
        {
            UpdateMaterializationProgress(completed, target, reused);
            ReportTransferProgress(0, true, fallbackProgress);
        }

        internal Task WaitWhilePausedAsync(CancellationToken cancellationToken)
        {
            return pauseToken.WaitWhilePausedAsync(cancellationToken);
        }

        internal Stream OpenCachedStream()
        {
            return new RemoteRangeCacheStream(cache, packageLength);
        }

        internal Task<byte[]> ReadBestRangeAsync(
            long offset,
            long maximumLength,
            bool retainInCache,
            CancellationToken cancellationToken)
        {
            if (maximumLength <= 0) throw new ArgumentOutOfRangeException(nameof(maximumLength));
            int limit = checked((int)Math.Min(MaximumSingleRangeLength, maximumLength));
            int cachedPrefix = cache.GetCachedPrefixLength(offset, limit);
            if (cachedPrefix > 0)
            {
                return ReadRangeAsync(offset, cachedPrefix, retainInCache, cancellationToken);
            }
            long? nextCachedOffset = cache.GetNextSegmentOffset(offset, checked(offset + limit));
            int length = nextCachedOffset.HasValue
                ? checked((int)(nextCachedOffset.Value - offset))
                : limit;
            return ReadRangeAsync(offset, length, retainInCache, cancellationToken);
        }

        internal async Task<byte[]> ReadRangeAsync(
            long offset,
            int length,
            bool retainInCache,
            CancellationToken cancellationToken)
        {
            ValidateRange(offset, length);
            await pauseToken.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
            byte[] cached = new byte[length];
            if (cache.TryCopy(offset, cached, 0, length))
            {
                return cached;
            }

            Exception lastException = null;
            int failures = 0;
            int completed = 0;
            bool recovering = false;
            Stopwatch recovery = Stopwatch.StartNew();
            int resumeVersion = pauseToken.ResumeVersion;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await pauseToken.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
                if (pauseToken.ResumeVersion != resumeVersion)
                {
                    resumeVersion = pauseToken.ResumeVersion;
                    recovery.Restart();
                }
                try
                {
                    int destinationOffset = completed;
                    await ReadRangeOnceAsync(
                        checked(offset + completed),
                        checked(length - completed),
                        cached,
                        destinationOffset,
                        recovering,
                        () => recovering = false,
                        read =>
                        {
                            completed += read;
                            failures = 0;
                            recovery.Restart();
                            ReportTransferProgress(completed);
                        },
                        cancellationToken).ConfigureAwait(false);
                    if (completed != length)
                    {
                        throw new InvalidDataException("增量 Range 读取完成长度与请求长度不一致。");
                    }
                    if (retainInCache) cache.Add(offset, cached);
                    ReportTransferProgress(completed, true);
                    return cached;
                }
                catch (ArtifactPipeline.DownloadPausedException)
                {
                    ResetSpeedSample();
                    await pauseToken.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
                    pipeline.LogMessage("下载已继续，正在立即重建增量 Range 请求。");
                    ReportDownloadState(
                        "正在重新连接",
                        "已收到继续下载请求，正在立即重建微软 CDN Range 请求。");
                    recovery.Restart();
                }
                catch (ArtifactPipeline.DownloadRetryRequestedException)
                {
                    ResetSpeedSample();
                    pipeline.LogMessage("已收到立即重试请求，正在重建增量 Range 请求。");
                    ReportDownloadState(
                        "正在重新连接",
                        "已中断当前网络探测，正在立即重建微软 CDN Range 请求。");
                    recovery.Restart();
                }
                catch (Exception exception) when (ArtifactPipeline.IsRetryableDownloadException(exception, cancellationToken))
                {
                    lastException = exception;
                    failures++;
                    recovering = true;
                    ResetSpeedSample();
                    if (recovery.Elapsed >= pipeline.DownloadRecoveryWindow) break;
                    TransientHttpRequestException transient = exception as TransientHttpRequestException;
                    TimeSpan delay = pipeline.GetDownloadRetryDelay(
                        failures,
                        transient == null ? null : transient.RetryAfter);
                    bool internetAvailable = pipeline.HasInternetAccess;
                    pipeline.LogMessage(string.Format(
                        CultureInfo.InvariantCulture,
                        internetAvailable
                            ? "增量 Range {0}-{1} 读取失败，将在 {2:F1} 秒后从已接收位置重试：{3}"
                            : "增量 Range {0}-{1} 读取失败，已保留已接收字节并等待系统网络恢复：{3}",
                        checked(offset + completed),
                        checked(offset + length - 1),
                        delay.TotalSeconds,
                        exception.Message));
                    ReportDownloadState(
                        internetAvailable
                            ? "微软 CDN 暂不可达，已自动暂停"
                            : "网络不可用，已自动暂停",
                        string.Format(
                            CultureInfo.InvariantCulture,
                            internetAvailable
                                ? "增量断点已自动保留，{0:F1} 秒后进行第 {1} 次探测；网络变化会立即唤醒。"
                                : "增量断点已自动保留，正在监听系统网络恢复；恢复后立即进行第 {1} 次探测。",
                            delay.TotalSeconds,
                            failures),
                        true);
                    await pipeline.WaitForDownloadRetryAsync(delay, pauseToken, cancellationToken).ConfigureAwait(false);
                }
            }
            throw new IOException(
                "微软 CDN Range 读取在恢复窗口内持续失败：" +
                (lastException == null ? "未知网络错误" : lastException.Message),
                lastException);
        }

        private async Task<byte[]> ReadRangeOnceAsync(
            long offset,
            int length,
            byte[] destination,
            int destinationOffset,
            bool recovering,
            Action connected,
            Action<int> bytesReceived,
            CancellationToken cancellationToken)
        {
            long end = checked(offset + length - 1);
            using (HttpResponseMessage response = await pipeline.SendRangeRequestAsync(
                url,
                offset,
                end,
                pauseToken,
                cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Increment(ref requestCount);
                if (HttpRetryPolicy.IsTransientStatus(response.StatusCode))
                {
                    throw new TransientHttpRequestException(
                        "微软 CDN 返回可重试的 Range HTTP 状态：" + (int)response.StatusCode + "。",
                        response.StatusCode,
                        HttpRetryPolicy.GetRetryAfter(response.Headers));
                }
                if (response.StatusCode != HttpStatusCode.PartialContent)
                {
                    throw new InvalidDataException(
                        "微软 CDN 未接受明确 Range 请求，HTTP=" + (int)response.StatusCode + "。");
                }
                if (response.Content == null)
                {
                    throw new InvalidDataException("微软 CDN Range 响应缺少内容。");
                }
                ValidateResponseRange(response, offset, end, packageLength, length);
                ValidateEntityTag(response.Headers.ETag);
                if (recovering)
                {
                    pipeline.LogMessage("微软 CDN 已接受新的 Range 请求，增量下载已自动继续。");
                    ReportDownloadState(
                        "网络已恢复，继续增量下载",
                        "微软 CDN 已接受新的 Range 请求，正在接收已保留断点之后的数据。");
                }
                if (connected != null) connected();

                int total = 0;
                byte[] buffer = new byte[Math.Min(1024 * 1024, Math.Max(1, length))];
                using (Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                {
                    while (true)
                    {
                        int read = await pipeline.ReadDownloadChunkAsync(
                            input,
                            buffer,
                            pauseToken,
                            cancellationToken).ConfigureAwait(false);
                        if (read <= 0) break;
                        Interlocked.Add(ref networkBytesRead, read);
                        if (read > length - total)
                        {
                            throw new InvalidDataException("微软 CDN Range 响应超过请求区间。");
                        }
                        Buffer.BlockCopy(buffer, 0, destination, checked(destinationOffset + total), read);
                        total += read;
                        if (bytesReceived != null) bytesReceived(read);
                    }
                }
                if (total != length)
                {
                    throw new ArtifactPipeline.DownloadTransportException(string.Format(
                        CultureInfo.InvariantCulture,
                        "微软 CDN Range 响应提前结束，预期 {0} 字节，实际 {1} 字节。",
                        length,
                        total));
                }
                return destination;
            }
        }

        private void ReportTransferProgress(
            int inFlightBytes,
            bool force = false,
            IProgress<OperationProgress> fallbackProgress = null)
        {
            IProgress<OperationProgress> reporter = progress ?? fallbackProgress;
            if (reporter == null || targetBytes <= 0) return;

            TimeSpan now = transferClock.Elapsed;
            TimeSpan sampleElapsed = now - speedSampleTime;
            if (sampleElapsed >= TimeSpan.FromMilliseconds(200))
            {
                long currentNetworkBytes = Interlocked.Read(ref networkBytesRead);
                long sampleBytes = Math.Max(0, currentNetworkBytes - speedSampleNetworkBytes);
                double instantBytesPerSecond = sampleBytes / Math.Max(0.001, sampleElapsed.TotalSeconds);
                smoothedBytesPerSecond = smoothedBytesPerSecond <= 0
                    ? instantBytesPerSecond
                    : smoothedBytesPerSecond * 0.65d + instantBytesPerSecond * 0.35d;
                speedSampleNetworkBytes = currentNetworkBytes;
                speedSampleTime = now;
            }

            long transferred = Math.Min(targetBytes, checked(completedTargetBytes + inFlightBytes));
            int percent = (int)Math.Min(100, transferred * 100L / targetBytes);
            if (!force && lastTransferPercent >= 0 &&
                now - lastProgressReportTime < TimeSpan.FromMilliseconds(250))
            {
                return;
            }
            lastTransferPercent = percent;
            lastProgressReportTime = now;

            string speed = smoothedBytesPerSecond > 0
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    " · {0:F1} MiB/s",
                    smoothedBytesPerSecond / 1048576d)
                : " · 正在测速";
            string remaining = smoothedBytesPerSecond > 0 && transferred < targetBytes
                ? " · 预计剩余 " + FormatRemaining(
                    TimeSpan.FromSeconds((targetBytes - transferred) / smoothedBytesPerSecond))
                : string.Empty;
            reporter.Report(new OperationProgress(
                "增量获取微软官方程序包",
                10 + (int)(percent * 45L / 100L),
                string.Format(
                    CultureInfo.InvariantCulture,
                    "目标补集 {0:F1} / {1:F1} MiB{2}{3} · 已复用 {4:F1} MiB",
                    transferred / 1048576d,
                    targetBytes / 1048576d,
                    speed,
                    remaining,
                    reusedBytes / 1048576d),
                true,
                percent));
        }

        private void ResetSpeedSample()
        {
            speedSampleNetworkBytes = Interlocked.Read(ref networkBytesRead);
            speedSampleTime = transferClock.Elapsed;
            smoothedBytesPerSecond = 0;
            lastProgressReportTime = TimeSpan.Zero;
        }

        private static string FormatRemaining(TimeSpan remaining)
        {
            if (remaining.TotalHours >= 1)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} 小时 {1} 分",
                    (int)remaining.TotalHours,
                    remaining.Minutes);
            }
            if (remaining.TotalMinutes >= 1)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} 分 {1} 秒",
                    (int)remaining.TotalMinutes,
                    remaining.Seconds);
            }
            return Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds))
                .ToString(CultureInfo.InvariantCulture) + " 秒";
        }

        private void ReportDownloadState(string message, string detail, bool networkWaiting = false)
        {
            if (progress == null) return;
            int percent = targetBytes <= 0
                ? 0
                : (int)Math.Min(100, completedTargetBytes * 100L / targetBytes);
            string progressDetail = targetBytes <= 0
                ? detail
                : string.Format(
                    CultureInfo.InvariantCulture,
                    "目标补集 {0:F1} / {1:F1} MiB · 已复用 {2:F1} MiB。{3}",
                    completedTargetBytes / 1048576d,
                    targetBytes / 1048576d,
                    reusedBytes / 1048576d,
                    detail);
            progress.Report(new OperationProgress(
                message,
                10 + (int)(percent * 45L / 100L),
                progressDetail,
                true,
                percent,
                networkWaiting));
        }

        private void ValidateRange(long offset, int length)
        {
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (length <= 0 || length > MaximumSingleRangeLength)
            {
                throw new ArgumentOutOfRangeException(nameof(length), "单次 Range 长度必须位于 1 到 16 MiB 之间。");
            }
            if (offset > packageLength || length > packageLength - offset)
            {
                throw new InvalidDataException("Range 请求超出目标 MSIX 范围。");
            }
        }

        private static void ValidateResponseRange(
            HttpResponseMessage response,
            long expectedStart,
            long expectedEnd,
            long expectedTotal,
            int expectedLength)
        {
            ContentRangeHeaderValue range = response.Content.Headers.ContentRange;
            if (range == null || !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase) ||
                !range.From.HasValue || range.From.Value != expectedStart ||
                !range.To.HasValue || range.To.Value != expectedEnd ||
                !range.Length.HasValue || range.Length.Value != expectedTotal)
            {
                throw new InvalidDataException("微软 CDN 返回的 Content-Range 与明确请求区间不一致。");
            }
            long? contentLength = response.Content.Headers.ContentLength;
            if (contentLength.HasValue && contentLength.Value != expectedLength)
            {
                throw new InvalidDataException("微软 CDN Range Content-Length 与请求长度不一致。");
            }
        }

        private void ValidateEntityTag(EntityTagHeaderValue entityTag)
        {
            string current = entityTag != null && !entityTag.IsWeak ? entityTag.Tag : null;
            if (strongEntityTag == null)
            {
                if (current != null) strongEntityTag = current;
                return;
            }
            if (current == null || !string.Equals(strongEntityTag, current, StringComparison.Ordinal))
            {
                throw new InvalidDataException("同一次增量任务中的微软 CDN 强 ETag 发生变化或消失。");
            }
        }
    }

    internal sealed class RemoteRangeCache
    {
        private readonly object sync = new object();
        private readonly List<RangeSegment> segments = new List<RangeSegment>();

        internal void Add(long offset, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) throw new ArgumentException("Range 缓存段不能为空。", nameof(bytes));
            lock (sync)
            {
                segments.Add(new RangeSegment(offset, bytes));
            }
        }

        internal bool TryCopy(long offset, byte[] destination, int destinationOffset, int count)
        {
            if (count == 0) return true;
            long requestedEnd = checked(offset + count);
            long cursor = offset;
            int written = 0;
            lock (sync)
            {
                while (cursor < requestedEnd)
                {
                    RangeSegment best = null;
                    foreach (RangeSegment segment in segments)
                    {
                        if (segment.Offset <= cursor && segment.End > cursor &&
                            (best == null || segment.End > best.End))
                        {
                            best = segment;
                        }
                    }
                    if (best == null) return false;
                    int sourceOffset = checked((int)(cursor - best.Offset));
                    int copied = checked((int)Math.Min(best.End - cursor, requestedEnd - cursor));
                    Buffer.BlockCopy(best.Bytes, sourceOffset, destination, destinationOffset + written, copied);
                    cursor += copied;
                    written += copied;
                }
            }
            return true;
        }

        internal int GetCachedPrefixLength(long offset, int maximumLength)
        {
            long requestedEnd = checked(offset + maximumLength);
            long cursor = offset;
            lock (sync)
            {
                while (cursor < requestedEnd)
                {
                    RangeSegment best = null;
                    foreach (RangeSegment segment in segments)
                    {
                        if (segment.Offset <= cursor && segment.End > cursor &&
                            (best == null || segment.End > best.End))
                        {
                            best = segment;
                        }
                    }
                    if (best == null) break;
                    cursor = Math.Min(best.End, requestedEnd);
                }
            }
            return checked((int)(cursor - offset));
        }

        internal long? GetNextSegmentOffset(long offset, long end)
        {
            long? next = null;
            lock (sync)
            {
                foreach (RangeSegment segment in segments)
                {
                    if (segment.Offset > offset && segment.Offset < end &&
                        (!next.HasValue || segment.Offset < next.Value))
                    {
                        next = segment.Offset;
                    }
                }
            }
            return next;
        }

        private sealed class RangeSegment
        {
            internal RangeSegment(long offset, byte[] bytes)
            {
                Offset = offset;
                Bytes = bytes;
                End = checked(offset + bytes.LongLength);
            }

            internal long Offset { get; private set; }
            internal long End { get; private set; }
            internal byte[] Bytes { get; private set; }
        }
    }

    internal sealed class RemoteRangeCacheStream : Stream
    {
        private readonly RemoteRangeCache cache;
        private readonly long length;
        private long position;

        internal RemoteRangeCacheStream(RemoteRangeCache cache, long length)
        {
            this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
            this.length = length;
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return true; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return length; } }
        public override long Position
        {
            get { return position; }
            set
            {
                if (value < 0 || value > length) throw new ArgumentOutOfRangeException(nameof(value));
                position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (position >= length) return 0;
            int requested = checked((int)Math.Min(count, length - position));
            if (!cache.TryCopy(position, buffer, offset, requested))
            {
                throw new InvalidDataException("远程 MSIX 解析访问了尚未取得的 Range。");
            }
            position += requested;
            return requested;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target;
            switch (origin)
            {
                case SeekOrigin.Begin: target = offset; break;
                case SeekOrigin.Current: target = checked(position + offset); break;
                case SeekOrigin.End: target = checked(length + offset); break;
                default: throw new ArgumentOutOfRangeException(nameof(origin));
            }
            Position = target;
            return position;
        }

        public override void Flush() { }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }
}
