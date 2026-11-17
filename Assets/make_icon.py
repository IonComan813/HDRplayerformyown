"""手绘场记板图标：在 1024 画布上绘制，再缩小生成各尺寸。
用法：python make_icon.py Assets preview.png（需要 Pillow）"""
from PIL import Image, ImageDraw, ImageChops
import sys, os

S = 1024
k = S / 256  # 设计稿按 256 单位

def U(*v): return [round(x * k) for x in v]

def vgrad(w, h, top, bottom):
    g = Image.new("RGBA", (w, h))
    d = ImageDraw.Draw(g)
    for y in range(h):
        t = y / max(1, h - 1)
        c = tuple(round(top[i] + (bottom[i] - top[i]) * t) for i in range(4))
        d.line([(0, y), (w, y)], fill=c)
    return g

def rounded_mask(box, r):
    m = Image.new("L", (S, S), 0)
    ImageDraw.Draw(m).rounded_rectangle(box, radius=r, fill=255)
    return m

def striped_bar(box, r, stripe=30, slant=22, light=(245, 245, 247, 255), dark=(34, 34, 40, 255)):
    """带斜条纹的拍板条。"""
    layer = Image.new("RGBA", (S, S), dark)
    d = ImageDraw.Draw(layer)
    x0, y0, x1, y1 = box
    w = U(stripe)[0]; s = U(slant)[0]
    x = x0 - 2 * w
    i = 0
    while x < x1 + 2 * w:
        if i % 2 == 0:
            d.polygon([(x + s, y0), (x + s + w, y0), (x + w - s, y1), (x - s, y1)], fill=light)
        x += w; i += 1
    out = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    out.paste(layer, (0, 0), rounded_mask(box, r))
    return out

def shadow(layer, offset=(0, 10), alpha=90):
    a = layer.split()[3].point(lambda v: v * alpha // 255)
    sh = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    sh.putalpha(a)
    sh = sh.transform((S, S), Image.AFFINE, (1, 0, -U(offset[0])[0], 0, 1, -U(offset[1])[0]))
    from PIL import ImageFilter
    return sh.filter(ImageFilter.GaussianBlur(U(6)[0]))

def draw(stripe=30, slant=22):
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    # ---- 机身 ----
    body_box = U(26, 104, 230, 228)
    body = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    body.paste(vgrad(S, S, (66, 68, 78, 255), (28, 29, 36, 255)), (0, 0), rounded_mask(body_box, U(26)[0]))
    ImageDraw.Draw(body).rounded_rectangle(body_box, radius=U(26)[0], outline=(255, 255, 255, 40), width=U(2)[0])

    # ---- 下方固定条纹条（贴在机身顶部）----
    low_box = U(26, 104, 230, 140)
    low = striped_bar(low_box, U(10)[0], stripe=stripe, slant=slant)

    # ---- 上方拍板（绕左侧铰链张开）----
    up_box = U(26, 60, 230, 96)
    up = striped_bar(up_box, U(10)[0], stripe=stripe, slant=slant)
    hinge = tuple(U(38, 100))
    up = up.rotate(17, resample=Image.BICUBIC, center=hinge)

    # ---- 机身上的播放三角（HDR 琥珀 → 橙色渐变）----
    tri = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    tri_mask = Image.new("L", (S, S), 0)
    cx, cy = U(128, 184)
    ImageDraw.Draw(tri_mask).polygon([tuple(U(108, 156)), tuple(U(108, 212)), tuple(U(156, 184))], fill=255)
    tri.paste(vgrad(S, S, (255, 214, 64, 255), (255, 112, 24, 255)), (0, 0), tri_mask)

    for layer in (body, low):
        img.alpha_composite(shadow(layer, (0, 6), 70))
        img.alpha_composite(layer)
    img.alpha_composite(tri)
    img.alpha_composite(shadow(up, (0, 6), 80))
    img.alpha_composite(up)
    # 铰链
    hd = ImageDraw.Draw(img)
    r = U(9)[0]
    hd.ellipse([hinge[0] - r, hinge[1] - r, hinge[0] + r, hinge[1] + r], fill=(200, 200, 206, 255), outline=(40, 40, 46, 255), width=U(2)[0])

    return img

big = draw()
small = draw(stripe=50, slant=26)   # 小尺寸用更宽的条纹，避免糊成一片
def pick(sz): return (small if sz <= 32 else big).resize((sz, sz), Image.LANCZOS)
out_dir = sys.argv[1]
os.makedirs(out_dir, exist_ok=True)
sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
pick(256).save(os.path.join(out_dir, "HdrPlayer.png"))
pick(256).save(os.path.join(out_dir, "HdrPlayer.ico"), sizes=[(s, s) for s in sizes],
               append_images=[pick(s) for s in sizes[:-1]])
# 预览图（仅供检查）
prev = Image.new("RGBA", (sum(sizes) + 20 * len(sizes) + 20, 300), (32, 32, 32, 255))
x = 20
for s in sizes:
    prev.alpha_composite(pick(s), (x, 20))
    x += s + 20
light = Image.new("RGBA", prev.size, (243, 243, 243, 255))
x = 20
for s in sizes:
    light.alpha_composite(pick(s), (x, 20))
    x += s + 20
both = Image.new("RGBA", (prev.width, 600)); both.paste(prev, (0, 0)); both.paste(light, (0, 300))
both.save(sys.argv[2])
