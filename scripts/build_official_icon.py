"""提取官方 Codex 托盘图标，必要时可从 MSIX 图片生成备用 ICO。"""

import argparse
import shutil
import struct
from pathlib import Path


SIZES = (16, 20, 24, 30, 32, 36, 40, 44, 48, 60, 64, 72, 80, 96, 256)


def read_png(path: Path) -> tuple[int, int, bytes]:
    data = path.read_bytes()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError(f"不是有效的 PNG：{path}")
    width, height = struct.unpack(">II", data[16:24])
    return width, height, data


def build_icon(assets: Path, output: Path) -> None:
    images = []
    for size in SIZES:
        source = assets / f"Square44x44Logo.targetsize-{size}_altform-lightunplated.png"
        width, height, data = read_png(source)
        if width != size or height != size:
            raise ValueError(f"图标尺寸不匹配：{source}，实际为 {width}x{height}")
        images.append((width, height, data))

    header_size = 6 + len(images) * 16
    offset = header_size
    entries = []
    payload = []
    for width, height, data in images:
        entries.append(
            struct.pack(
                "<BBBBHHII",
                0 if width == 256 else width,
                0 if height == 256 else height,
                0,
                0,
                1,
                32,
                len(data),
                offset,
            )
        )
        payload.append(data)
        offset += len(data)

    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_bytes(struct.pack("<HHH", 0, 1, len(images)) + b"".join(entries) + b"".join(payload))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--resources", type=Path, help="Codex app/resources 目录，优先复制托盘图标")
    parser.add_argument("--assets", type=Path, help="官方 MSIX 的 assets 目录，作为备用生成来源")
    parser.add_argument("--output", type=Path, required=True, help="输出 ICO 路径")
    args = parser.parse_args()
    if args.resources:
        candidates = (
            args.resources / "chatgpt-tray-light.ico",
            args.resources / "icon.ico",
        )
        source = next((path for path in candidates if path.is_file()), None)
        if source is None:
            parser.error(f"resources 目录中没有可用的官方 ICO：{args.resources}")
        args.output.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, args.output)
    elif args.assets:
        build_icon(args.assets, args.output)
    else:
        parser.error("必须提供 --resources 或 --assets")


if __name__ == "__main__":
    main()
