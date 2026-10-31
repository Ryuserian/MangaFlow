# -*- coding: utf-8 -*-
"""MangaFlow 아이콘 생성기 — 밋밋한 이모지 대신 브랜드 오렌지 스퀴클 + 말풍선·플로우 로고.

출력 (src/MangaFlow.App/Assets/):
  - icon.ico        : exe/창 아이콘 (16~256 멀티 프레임)
  - icon_16..256.png: 개별 크기 PNG
  - logo_256.png    : 스플래시/상단바 로고 (same artwork)

재실행:  python tools/make_icon.py
"""
import pathlib

from PIL import Image, ImageDraw

S = 1024          # 작업 해상도 (4x 강하刘畅보간으로 선명도 확보)
OUT = pathlib.Path(__file__).resolve().parent.parent / "src" / "MangaFlow.App" / "Assets"

# 브랜드 팔레트 (앱 accent #FF6D00 계열)
C_TOP = (255, 158, 64)     # 좌상단 밝은 오렌지
C_BOT = (255, 84, 0)       # 우하단 딥 오렌지
WHITE = (255, 255, 255)
INK = (255, 84, 0)         # 말풍선 내 텍스트 바

def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))

def rounded_mask(size, radius):
    m = Image.new("L", size, 0)
    ImageDraw.Draw(m).rounded_rectangle([0, 0, size[0] - 1, size[1] - 1], radius=radius, fill=255)
    return m

def make_master():
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    dr = ImageDraw.Draw(img)

    # ── 배경: 오렌지 대각선 그라데 스퀴클 ──
    pad, rad = 28, 232
    grad = Image.new("RGBA", (S, S))
    gd = ImageDraw.Draw(grad)
    for y in range(S):
        t = y / S
        gd.line([(0, y), (S, y)], fill=lerp(C_TOP, C_BOT, t) + (255,))
    bg_mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(bg_mask).rounded_rectangle([pad, pad, S - pad, S - pad], radius=rad, fill=255)
    img.paste(grad, (0, 0), bg_mask)

    # 상단부 은은한 광택
    shine = Image.new("L", (S, S), 0)
    sd = ImageDraw.Draw(shine)
    sd.ellipse([S * 0.02, S * -0.45, S * 0.98, S * 0.55], fill=26)
    img.paste(Image.new("RGBA", (S, S), WHITE + (26,)), (0, 0), shine)

    # ── 말풍선 (흰색, 꼬리 좌하단) ──
    bub = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    bd = ImageDraw.Draw(bub)
    bd.rounded_rectangle([172, 228, 852, 648], radius=136, fill=WHITE + (255,))
    bd.polygon([(312, 640), (448, 640), (330, 800)], fill=WHITE + (255,))
    bd.ellipse([306, 772, 354, 820], fill=WHITE + (255,))   # 꼬리 끝 라운딩
    img.alpha_composite(bub)

    # ── 재조판 텍스트 바 3줄 + 플로우 도트 ──
    bars = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    td = ImageDraw.Draw(bars)
    h, r = 56, 28
    td.rounded_rectangle([252, 318, 772, 318 + h], radius=r, fill=INK + (255,))
    td.rounded_rectangle([252, 420, 772, 420 + h], radius=r, fill=INK + (255,))
    td.rounded_rectangle([252, 522, 566, 522 + h], radius=r, fill=INK + (255,))
    cy = 522 + h // 2 + 2
    for cx, rr, a in ((610, 27, 200), (682, 17, 120), (738, 9, 60)):
        td.ellipse([cx - rr, cy - rr, cx + rr, cy + rr], fill=INK + (a,))
    img.alpha_composite(bars)

    return img

def main():
    OUT.mkdir(parents=True, exist_ok=True)
    master = make_master()

    sizes_png = [16, 24, 32, 48, 64, 128, 256]
    for s in sizes_png:
        master.resize((s, s), Image.LANCZOS).save(OUT / f"icon_{s}.png")
    master.resize((256, 256), Image.LANCZOS).save(OUT / "logo_256.png")
    master.save(OUT / "icon.ico", format="ICO",
                sizes=[(s, s) for s in (16, 24, 32, 48, 64, 128, 256)])

    print("icons written ->", OUT)

if __name__ == "__main__":
    main()
