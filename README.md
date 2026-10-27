# Manga Translator — 日本語 → 한국어 만화 번역기 (local-first)

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Avalonia](https://img.shields.io/badge/UI-Avalonia_12-1A9BE0?style=flat)](https://avaloniaui.net/)
[![OS](https://img.shields.io/badge/OS-Windows%20·%20macOS%20·%20Linux-3B82F6?style=flat)](#-os--하드웨어-지원)
[![GPU](https://img.shields.io/badge/GPU-CUDA%20·%20Metal%20·%20CPU_폴백-76B900?style=flat)](#-os--하드웨어-지원)
[![LLM](https://img.shields.io/badge/LLM-Qwen3%20GGUF-F59E0B?style=flat)](#-모델-카탈로그--라이선스)
[![OCR](https://img.shields.io/badge/OCR-Qwen3--VL%20·%20PP--OCRv4%20det-8B5CF6?style=flat)](#-모델-카탈로그--라이선스)
[![Inpaint](https://img.shields.io/badge/Inpaint-LaMa-EC4899?style=flat)](#-모델-카탈로그--라이선스)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg?style=flat)](LICENSE)
[![Models](https://img.shields.io/badge/models-not%20included%20·%20auto--download-64748B?style=flat)](#-모델-자동-다운로더)

만화 페이지의 일본어 말풍선을 **인식 → 텍스트 제거 → 번역 → 같은 위치에 한국어 재조판**하는 데스크톱 앱.
핵심 파이프라인은 전부 **온디바이스**(GPU 가속), 인터넷은 웹 번역 엔진·모델 최초 다운로드 때만 씁니다.

```
① 이미지 로드 → ② OCR 감지 → ③ 텍스트 제거(LaMa inpaint) → ④ 번역(말투 반영) → ⑤ 한국어 재조판
```

## 🖼️ 결과 샘플

| 원문 (日本語) | 번역 결과 (한국어) |
|:---:|:---:|
| ![원문](sample/before.png) | ![번역 결과](sample/after.png) |

*세로 원문도 한국어는 **가로 재조판**(행 위→아래) — 말줄임표·문장부호 단위 어구 분할 + 어절 줄바꿈, 글자 크기 원본 매칭.*

- **OSS**: 앱 코드는 MIT. 동봉 이미지·모델 없음 — 첫 실행 시 공식 소스에서 자동 다운로드
- **GUI**: Avalonia 12 (라이트 테마 · 마법사 wizard UI) · 반응형 캔버스 오버레이 편집

---

## ✨ 기능

### 마법사 워크플로 (wizard)
우측 패널이 4단계 페이지로 전환됩니다 — **① 열기 → ② 감지 → ③ 편집 → ④ 실행·결과**
- 상단 스텝바: 진행 상태 색상 표시(현재=주황/완료=녹색), 방문했던 단계는 클릭으로 복귀
- 하단 `◀ 이전 / 다음 ▶` 버튼, 단계 자동 전환(감지 완료 → 편집, 적용 완료 → 결과)

### 말풍선 편집
- OCR가 만든 사각형 인스턴스를 **캔버스에서 클릭-드래그로 선택·이동·리사이즈**(코너 4개 핸들)
- ☑ 체크된 말풍선만 제거·번역 대상 — 원문 직접 수정, **글자 크기 노브**(박스 내 strict-fit)
- **읽기 방향 ComboBox(4택)** — 가로 왼쪽/오른쪽·세로 왼쪽/오른쪽. 번역 재조판은 한국어 기본(가로 왼쪽)
- **말투 ComboBox(3택)** — 자동 / 여성형 / 남성형
- **사각형별 🔄 재인식 버튼** — 그 말풍선 하나만 OCR 재판독해 원문 복원
- **저신뢰(conf < 0.55) 항목은 빨간 경고 라벨** 자동 표시
- 겹치는 검출 박스 자동 억제(고신뢰·대구간 승자 기준 contained≥50%·IoU≥0.30 드롭)

### 한국어 재조판 규칙
- **항상 가로** — OCR이 세로로 감지해도 한국어 출력은 가로 행, 위→아래 순서 (`RENDER_VERT=1` env로만 세로 복원)
- **줄나눔 3단계 우선순위**:
  1. `/` 강제 개행 — 슬래쉬 위치에서 무조건 다음 줄(기호 제거)
  2. 어구 분할 — 말줄임표(…)는 단독 행, 문장부호(`. , ! ? ; :`) 뒤로 새 줄 (소수점 3.14는 보호)
  3. 어절 wrap — 폭까지 단위로 채우고 넘치면 단어째 다음 줄 (단어 자체가 넘을 때만 글자 절단)
- auto grow-until-fit — 원본 글자 크기에서 출발해 박스에 들어가는 최대까지 키움
- **자동 글자색 + 반대색 외곽선(halo)** — 회색 스크린톤 배경 위에서도 가독성 유지

### 말투(성격) 번역
말풍선마다 **자동(Auto) / 여성형 / 남성형** 선택 — 번역 엔진에 화자 힌트로 전달됩니다.
- 로컬 Qwen: 대사 뒤 `(여)`/`(남)` 마커로 프롬프트 주입 → 어투 분기 (여성형: 부드러운 종결·~잖아 / 남성형: ~다·~는데)
- **Auto = 마커 없이 원문(私/僕/あたし 등)으로 모델이 화자 추정** — 기본 동작
- CLI: `TRANSLATE_TONE=male|female` env로 페이지 전체 기본말투 강제
- 웹 시퀀스투시퀀스 엔진(Google/DeepL)은 미적용(인터페이스만 호환)

### 번역 엔진 런타임 전환 (추상화)
로컬 GGUF와 웹 API가 `ITranslationEngine` **동일 인터페이스** — GUI 콤보박스 또는 `TRANSLATE_ENGINE` env로 즉시 교체:

| 키 | 엔진 | 필요 | 특징 |
|---|---|---|---|
| `qwen_gguf` | **Qwen3 GGUF (로컬)** | 모델 다운로드(32B 권장·14B 선택) | 무료·비공개·고품질·**말투 반영** |
| `web_google` | Google 번역 | 없음 | 초고속·무키. 차단 네트워크에선 MyMemory 자동 폴백 |
| `web_deepl` | DeepL | `DEEPL_API_KEY` | 월 50만자 무료 |
| `web_openai` | GPT/OpenAI 호환 | `OPENAI_API_KEY` | `OPENAI_MODEL`/`OPENAI_BASE` env로 모델·베이스 URL 지정 |

> 잡방(채팅) 모델 오염 방어: 번호 지정 배치 프롬프트(`1. 대사` → `1. 번역`) + 줄 수 검증 +
> 불일치 줄만 개별 미니멀 프롬프트 폴백 — `Core/Translate/NumberedBatchProtocol` 공통 구현.

### OCR (감지 + 판독 분리)
- 감지: PP-OCRv4 det(전용) → 판독: Qwen3-VL(기본) 또는 PP-OCRv4 rec(`OCR_ENGINE=ppocr`)
- LaMa로 텍스트 제거 — OCR 검출 박스(+8px)만 content-fill, 박스 밖 픽셀 무손상

### 모델 자동 다운로더
앱이 부팅 때 카탈로그를 감사하고, 없는 모델이 있으면 **스플래시에서 목록+총 용량을 보여주고 다운로드**.
- 번역 GGUF 드롭다운 택1(기본 32B 권장 / 저사양 14B). 스킵 시 번역 엔진은 `web_google` 폴백으로 메인 진입
- `.part` 이어받기, sha256 검증, 백오프 재시도
- 모델 교체·추가 = 카탈로그(`ModelManager`) 한 줄 — 코드 무접촉
- VRAM 순차 상재: OCR(VLM)·번역(GGUF) 동시 상재 금지 — 스테이지 종료 시 언로드, 다음 사용 시 lazy 재로드

### 배치 모드 (Expander 편집 워크스페이스)
- 폴더/여러 파일 큐잉(png/jpg/jpeg/webp 재귀, 페이지 이름 순) → 같은 설정으로 연속 처리
- 각 파일 **Expander 카드** — 펼치면 캔버스에 올라오고(lazy 디코딩+자동 감지) 장별 말풍선 편집. 접으면 비트맵 회수(편집 유지)
- 펼쳤던(편집한) 장은 편집 존중 Apply, 안 펼친 장은 자동 Run. ☐ 전 해제 = "보존"
- 출력 `out/<name>_ko.png`, 진행률 창 전체 오버레이 + 취소

---

## 🚀 빠른 시작

```bash
git clone <repo> && cd Manga
dotnet build MangaTranslator.slnx
dotnet run --project src/MangaTranslator.App          # GUI
```

첫 실행: 스플래시 → **📥 모델 다운로드 선택** (OCR rec Qwen3-VL ≈18GB 필수 포함. 번역은 **32B 권장 ≈22GB** — 드롭다운에서 14B ≈13GB 선택 가능) → 엔진 자동 로드 → 이미지 열면 끝. 큰 GGUF는 첫 인식·번역 시점에 GPU에 오름(순차 상재). ✕는 진행 중 앱 종료(이어받기 유지).

```bash
# CLI 검증 (GUI 없이)
dotnet run --project src/MangaTranslator.App -- --smoke
dotnet run --project src/MangaTranslator.App -- --pipeline <in.jpg> [out.png]   # ①→⑤ 종단 (중간산물 <in>.clean.png)
dotnet run --project src/MangaTranslator.App -- --audit            # 모델 감사
dotnet run --project src/MangaTranslator.App -- --ocrtest <in.png> [annot.png]  # OCR 실모델 검출+리포트
dotnet run --project src/MangaTranslator.App -- --inpaintdiff <orig> <clean>     # inpaint 박스 밖 침범 계측
```

**요구**: .NET 8 SDK (런타임 8/9/10 호환, `RollForward=LatestMajor`). 모델 캐시 `MANGA_MODELS_DIR` env(기본 `models/`).

### 튜닝 env (렌더·성능)
| env | 기본 | 역할 |
|---|---|---|
| `RENDER_VERT` | off | `1` = 만화식 세로 재조판 복원 |
| `TRANSLATE_TONE` | off | `male`/`female` — CLI 페이지 기본말투 |
| `RENDER_SCALE` | 1.4 | 재조판 글자 전역 배율 |
| `RENDER_OUTLINE` | 0.18 | halo 두께(fs×배율, 0=off) |
| `RENDER_INFLATE` | 4 | 렌더 박스 사방 팽창(px) |
| `RENDER_WSLACK` | 1.0 | 가로 허용 폭 배율 |
| `TRANSLATE_ENGINE` | auto | 번역 엔진 키 강제 |
| `OCR_ENGINE` | vlm | `ppocr` = rec를 PP-OCR로 |
| `LLAMA_NGL` | 99 | GGUF GPU 층 수(0=CPU) |
| `ORT_CPU` | off | ONNX CPU 강제 |

---

## 🖥 OS / 하드웨어 지원

| 플랫폼 | 상태 | 가속 |
|---|---|---|
| **macOS (Apple Silicon)** | ✅ 실측 완료 | **Metal** (llama.cpp ggml-metal 전 층 오프로드) · ONNX CPU |
| **Windows x64** | ✅ 개발 1순위 | **CUDA 13** (RTX 5090 sm_120) / CPU 폴백 |
| **Linux x64** | 🧪 미검증 | CUDA 13 / CPU 폴백 |

- GPU 자동 정책: macOS=Metal / Windows·Linux=CUDA 우선, 초기화 실패 시 **CPU 자동 폴백**
- **⚡ CUDA DLL — 설치 없이 GPU(portable)**:
  - Release zip: `ggml-cuda.dll`·`cublas64_13.dll`·cuDNN 9 세트(≈1.5GB) 동봉 — Toolkit 설치 없이 즉시 GPU. 모델은 미포함(첫 실행 다운로더)
  - 직접 빌드: CUDA Toolkit+cuDNN 설치 PC는 PATH 자동주입, 미설치 PC는 CPU 폴백
- **VRAM/RAM 권장**: 번역 32B q4(≈18.4GiB)= 32GB 카드 / 14B q4(≈8.4GiB)= 12GB+ · OCR VLM(≈18GB)과 **순차 상재**로 32GB에서 동작 · det+LaMa ONNX 도합 ≈0.5GB(웹 번역 선택 시 로컬 번역 모델 불필요)

---

## 📦 모델 카탈로그 & 라이선스

앱 코드 **MIT**. 모델은 저장소 포함 **안 함** — 최초 실행 시 표기 소스에서 내려받아 `models/`에 캐시(카탈로그에 sha256 고정).

| 모델 | 용도 | 소스 | 라이선스 |
|---|---|---|---|
| PP-OCRv4 **det** | 말풍선 검출 | `SWHL/RapidOCR` | Apache-2.0 |
| **Qwen3-VL-30B-A3B** (GGUF+mmproj) | rec 판독 | `Qwen/Qwen3-VL-30B-A3B-Instruct-GGUF` | Apache-2.0 |
| LaMa fp32 (ONNX) | 텍스트 제거 | `Carve/LaMa-ONNX` | Apache-2.0 |
| Qwen3 q4_k_m | 번역 — **둘 중 하나**(기본 32B·저사양 14B) | `ggml-org/Qwen3-32B-GGUF` · `unsloth/Qwen3-14B-GGUF` | Apache-2.0 |
| Noto Sans KR Regular/Bold | 한글 조판 | `googlefonts/noto-cjk` | OFL |

**주요 라이브러리**: Avalonia 12.1 / ReactiveUI 26 (MIT) · LLamaSharp 0.30 (MIT, Metal/CUDA) · Microsoft.ML.OnnxRuntime 1.31 (MIT) · SkiaSharp 2.88 (MIT) · SixLabors.ImageSharp 2.1.9 (Apache-2.0)

웹 번역 엔진(Google/DeepL/OpenAI) 사용 시 각 서비스 이용약관·API 키 필요.

---

## 🗂 구조

```
src/
├─ MangaTranslator.Core          # 의존성 0: ImageData·TextRegion(Tone 포함)·인터페이스·파이프라인·NumberedBatchProtocol
├─ MangaTranslator.ModelManager  # 카탈로그(repo/파일/sha256) — 모델 다운로드 단일 진실원
├─ MangaTranslator.Plugins.*     # Ocr.PpOcr · Ocr.VlmOcr · Ocr.VlmQwen · Inpaint.Lama
│                                # Translate.QwenGguf · Translate.WebApi(google/deepl/openai) · Render.Skia
└─ MangaTranslator.App           # Avalonia GUI: 스플래시(엔진 프리로드+다운로더) /
                                 #   PageEditor(편집 units: 캔버스·감지·말투·방향·크기) — 싱글=마법사,
                                 #   배치=파일별 Expander가 PageEditor 재사용 · smoke CLI(--pipeline/--ocrtest/…)
```

엔진 교체: `EngineContext` 팩토리 등록 + 카탈로그 키 — 파이프라인·UI 무접촉.
큰 GGUF 엔진(llama.cpp)은 llama-server subprocess 없는 in-process 직접 로드.

## 라이선스
MIT (앱 코드). 모델 라이선스는 위 표를 따릅니다 — 배포 전 각 모델 페이지를 확인하세요.
