using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace CodexPortableManager
{
    internal static class HttpRetryPolicy
    {
        internal const int MaximumAttempts = 3;
        internal const int MaximumRedirects = 5;
        internal static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromMilliseconds(500);
        private static readonly object RandomLock = new object();
        private static readonly Random Random = new Random();

        internal static bool IsTransientStatus(HttpStatusCode statusCode)
        {
            return statusCode == HttpStatusCode.RequestTimeout ||
                (int)statusCode == 429 ||
                statusCode == HttpStatusCode.InternalServerError ||
                statusCode == HttpStatusCode.BadGateway ||
                statusCode == HttpStatusCode.ServiceUnavailable ||
                statusCode == HttpStatusCode.GatewayTimeout;
        }

        internal static bool IsRedirectStatus(HttpStatusCode statusCode)
        {
            int value = (int)statusCode;
            return value == 301 || value == 302 || value == 303 || value == 307 || value == 308;
        }

        internal static bool IsTransientTransportException(
            Exception exception,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            return exception is HttpRequestException ||
                exception is WebException ||
                exception is TaskCanceledException;
        }

        internal static TimeSpan? GetRetryAfter(HttpResponseHeaders headers)
        {
            if (headers == null || headers.RetryAfter == null)
            {
                return null;
            }
            if (headers.RetryAfter.Delta.HasValue)
            {
                return NormalizeServerDelay(headers.RetryAfter.Delta.Value);
            }
            if (headers.RetryAfter.Date.HasValue)
            {
                return NormalizeServerDelay(headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow);
            }
            return null;
        }

        internal static Task DelayAsync(
            int retryIndex,
            TimeSpan initialDelay,
            TimeSpan? serverDelay,
            CancellationToken cancellationToken)
        {
            TimeSpan delay = serverDelay ?? GetExponentialDelay(retryIndex, initialDelay);
            return delay <= TimeSpan.Zero
                ? Task.FromResult(0)
                : Task.Delay(delay, cancellationToken);
        }

        private static TimeSpan GetExponentialDelay(int retryIndex, TimeSpan initialDelay)
        {
            if (initialDelay <= TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }
            int exponent = Math.Max(0, Math.Min(4, retryIndex));
            double milliseconds = initialDelay.TotalMilliseconds * (1 << exponent);
            int jitter;
            lock (RandomLock)
            {
                jitter = Random.Next(0, 201);
            }
            return TimeSpan.FromMilliseconds(Math.Min(30000, milliseconds + jitter));
        }

        private static TimeSpan NormalizeServerDelay(TimeSpan delay)
        {
            if (delay <= TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }
            return delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay;
        }
    }

    internal sealed class TransientHttpRequestException : HttpRequestException
    {
        internal TransientHttpRequestException(
            string message,
            HttpStatusCode? statusCode,
            TimeSpan? retryAfter,
            Exception innerException = null)
            : base(message, innerException)
        {
            StatusCode = statusCode;
            RetryAfter = retryAfter;
        }

        internal HttpStatusCode? StatusCode { get; private set; }
        internal TimeSpan? RetryAfter { get; private set; }
    }
}
