"""Build native WixUI bitmaps from Tern's existing PNG; standard library only."""
from pathlib import Path
import struct
import zlib


def read_icon(path):
    data = path.read_bytes()
    if data[:8] != b'\x89PNG\r\n\x1a\n':
        raise ValueError('Installer icon must be a PNG')
    cursor = 8; compressed = bytearray(); width = height = channels = 0
    while cursor < len(data):
        length = struct.unpack_from('>I', data, cursor)[0]
        kind = data[cursor + 4:cursor + 8]; content = data[cursor + 8:cursor + 8 + length]
        if kind == b'IHDR':
            width, height, depth, color, compression, filtering, interlace = struct.unpack('>IIBBBBB', content)
            if depth != 8 or color not in (2, 6) or interlace or compression or filtering or not (0 < width <= 1024 and 0 < height <= 1024):
                raise ValueError('Use a noninterlaced 8-bit RGB/RGBA Tern icon, at most 1024 pixels')
            channels = 4 if color == 6 else 3
        elif kind == b'IDAT': compressed.extend(content)
        elif kind == b'IEND': break
        cursor += length + 12
    if not channels: raise ValueError('Missing icon dimensions')
    raw = zlib.decompress(compressed); stride = width * channels
    if len(raw) != (stride + 1) * height: raise ValueError('Invalid icon scanlines')
    rows = []; previous = bytearray(stride)
    for y in range(height):
        offset = y * (stride + 1); mode = raw[offset]; row = bytearray(raw[offset + 1:offset + stride + 1])
        if mode > 4: raise ValueError('Invalid PNG filter')
        for x in range(stride):
            left = row[x - channels] if x >= channels else 0
            up = previous[x]; corner = previous[x - channels] if x >= channels else 0
            if mode == 1: predictor = left
            elif mode == 2: predictor = up
            elif mode == 3: predictor = (left + up) // 2
            elif mode == 4:
                estimate = left + up - corner
                distances = (abs(estimate - left), abs(estimate - up), abs(estimate - corner))
                predictor = (left, up, corner)[distances.index(min(distances))]
            else: predictor = 0
            row[x] = (row[x] + predictor) & 255
        rows.append(row); previous = row
    return width, height, channels, rows


def render(icon, width, height, sidebar):
    source_width, source_height, channels, rows = icon
    pixels = bytearray(b'\xff' * (width * height * 3))
    if sidebar:
        for y in range(height):
            for x in range(164): pixels[(y * width + x) * 3:(y * width + x) * 3 + 3] = bytes((237, 246, 244))
    left, top, size = (34, 54, 96) if sidebar else (435, 5, 48)
    for y in range(size):
        sy = (y + .5) * source_height / size - .5
        y0 = max(0, int(sy)); y1 = min(source_height - 1, y0 + 1); fy = max(0, sy - y0)
        for x in range(size):
            sx = (x + .5) * source_width / size - .5
            x0 = max(0, int(sx)); x1 = min(source_width - 1, x0 + 1); fx = max(0, sx - x0)
            samples = ((x0, y0, (1-fx)*(1-fy)), (x1, y0, fx*(1-fy)), (x0, y1, (1-fx)*fy), (x1, y1, fx*fy))
            values = [sum(rows[py][px * channels + c] * weight for px, py, weight in samples) for c in range(channels)]
            alpha = values[3] / 255 if channels == 4 else 1
            destination = ((top + y) * width + left + x) * 3
            for c in range(3): pixels[destination+c] = round(values[c] * alpha + pixels[destination+c] * (1-alpha))
    return pixels


def write_images(path, width, height, pixels):
    stride = (width * 3 + 3) & ~3
    image = bytearray()
    for y in range(height - 1, -1, -1):
        row = pixels[y * width * 3:(y + 1) * width * 3]
        for x in range(0, len(row), 3): image.extend((row[x+2], row[x+1], row[x]))
        image.extend(b'\0' * (stride - width * 3))
    header = b'BM' + struct.pack('<IHHI', 54 + len(image), 0, 0, 54)
    header += struct.pack('<IiiHHIIiiII', 40, width, height, 1, 24, 0, len(image), 3780, 3780, 0, 0)
    path.write_bytes(header + image)
    def chunk(kind, data): return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data))
    scanlines = b''.join(b'\0' + pixels[y * width * 3:(y + 1) * width * 3] for y in range(height))
    path.with_suffix('.png').write_bytes(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 2, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(scanlines)) + chunk(b'IEND', b''))


def build_branding(destination):
    destination.mkdir(parents=True, exist_ok=True)
    icon = read_icon(Path(__file__).resolve().parents[1] / 'src/Tern.Desktop/Assets/tern.png')
    for name, width, height, sidebar in [('tern-banner.bmp', 493, 58, False), ('tern-dialog.bmp', 493, 312, True)]:
        write_images(destination / name, width, height, render(icon, width, height, sidebar))
