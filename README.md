# Ream.me is written by Korean, Japanese, and English

# Unity SRP 기반 실시간 하이브리드 워터마킹 성능 및 화질 분석

## 프로젝트 개요

본 프로젝트는 Unity Universal Render Pipeline (URP) 환경에서 실시간 디지털 워터마킹 기법의 성능 및 화질 영향을 분석하기 위해 개발되었습니다. 디지털 콘텐츠의 지적 재산권 보호 및 무단 복제 방지의 중요성이 커짐에 따라, 게임과 같은 실시간 상호작용형 콘텐츠에 효과적인 워터마킹 기술을 적용하는 방안을 탐구합니다.

## 연구 목표

* Unity URP 환경의 최종 렌더링 단계에 LSB (Least Significant Bit), DCT (Discrete Cosine Transform), DWT (Discrete Wavelet Transform) 기반 워터마킹 기법을 통합.
* 컴퓨트 셰이더를 활용하여 GPU 상에서 실시간 워터마크 삽입 연산 수행.
* 워터마크 적용 빈도(매 프레임 및 간헐적 적용) 및 워터마킹 강도가 렌더링 성능(프레임률, CPU/GPU 부하) 및 이미지 화질(PSNR, SSIM, MSE)에 미치는 영향 정량적으로 분석.
* 연산 부하와 시각적 충실도 간의 균형을 이루는 실시간 워터마킹 적용을 위한 기초 자료 제시.

## 기술 스택

* **게임 엔진:** Unity 2022.3.60f1
* **렌더 파이프라인:** Universal Render Pipeline (URP)
* **개발 도구:** Visual Studio 2022
* **성능 측정:** Unity Profiler (FPS, CPU Main Thread time, GPU Frame time)
* **이미지 화질 측정:** Python (Scikit-image for PSNR, SSIM, NumPy for MSE)
* **개발 언어:** C#, HLSL (Compute Shaders)

## 핵심 기능 및 구현 상세

본 프로젝트는 Unity URP 렌더링 파이프라인 내에 커스텀 Render Pass 형태로 워터마킹 기법을 구현했습니다. Post-Processing 이후의 최종 렌더 타겟에 직접 접근하여 워터마크 삽입 연산을 수행합니다[cite: 96].

### 1. LSB (Least Significant Bit) 워터마킹

* 최종 렌더 텍스처의 픽셀 컬러 채널(R, G, B) 중 Blue 채널의 최하위 비트를 미리 정해진 워터마크 비트 시퀀스로 대체하는 방식으로 구현되었습니다.
* **특징:** 구현이 단순하고 계산 효율성이 높습니다. 비가시성이 뛰어나지만, 이미지 압축, 필터링, 노이즈 삽입 등 픽셀 값에 변화를 주는 공격에 매우 취약합니다.

### 2. DCT (Discrete Cosine Transform) + Spread Spectrum 워터마킹

* 최종 렌더 텍스처 이미지를 8x8 크기의 블록으로 분할하고, 각 블록에 대해 2D DCT Type 2 변환을 적용합니다.
* 변환된 주파수 계수 중 워터마크 삽입에 사용될 대역의 계수(AC 계수)에 비밀 키 기반의 의사 난수(PN) 시퀀스와 워터마크 신호를 결합하여 추가합니다. 이후 역 DCT 변환을 통해 공간 영역으로 복원합니다.
* **특징:** 이미지 압축 표준(예: JPEG)에 널리 사용되며, 중간 주파수 대역 활용을 통해 워터마크의 견고성을 확보합니다. 확산 스펙트럼 기법을 통해 견고성을 증대시킵니다.

### 3. DWT (Discrete Wavelet Transform) + Spread Spectrum 워터마킹

* 최종 렌더 텍스처 이미지에 대해 2D Haar 웨이블릿 변환을 적용하여 LL, LH, HL, HH 등의 부대역으로 분해합니다. 본 실험에서는 8x8 블록을 사용하고, HH 부대역에만 워터마크 신호를 추가하여 계산 부하를 줄였습니다.
* 워터마크 삽입에 사용될 상세 부대역(HH)의 계수에 비밀 키 기반 PN 시퀀스와 워터마크 신호를 결합하여 추가합니다. 이후 역 DWT 변환을 통해 공간 영역으로 복원합니다.
* **특징:** 이미지의 공간적 지역성을 잘 반영하며, JPEG2000 압축 표준의 근간을 이룹니다. 확산 스펙트럼 기법을 통해 워터마크의 견고성을 확보합니다.

## 설치 및 실행 방법

1.  **Unity 설치:** Unity Hub를 통해 Unity 2022.3.60f1 버전을 설치합니다.
2.  **프로젝트 클론:**
    ```bash
    git clone [프로젝트_깃허브_URL]
    cd [프로젝트_폴더_이름]
    ```
3.  **Unity 프로젝트 열기:** Unity Hub에서 클론한 프로젝트 폴더를 열어줍니다.
4.  **Scene 실행:** `Assets/Scenes/` 경로에 있는 `[테스트_씬_이름].unity` 파일을 엽니다.
5.  **성능 측정:** Unity 에디터의 `Window > Analysis > Profiler` 메뉴를 통해 Profiler 창을 열고, 재생 버튼을 눌러 성능을 측정합니다.
6.  **화질 측정:** 화질 측정을 위한 Python 스크립트(`image_quality_analyzer.py` 등)는 `Assets/Scripts/ImageQuality/` 경로에 있습니다. Unity에서 워터마킹 전/후 이미지를 저장한 후, 해당 스크립트를 사용하여 PSNR, SSIM, MSE 값을 계산합니다.

## 주요 실험 결과

### 성능 분석 (FHD 및 4K 해상도)

* **적용 빈도의 중요성:** 워터마킹 적용 빈도가 증가할수록(즉, 1초 동안 워터마킹이 활성화되는 시간이 길어질수록) CPU Main Thread 및 GPU Frame 시간은 증가하고, 평균 FPS는 선형적으로 감소합니다. 이는 실시간 렌더링 성능에 직접적인 부하를 가함을 의미합니다.
* **간헐적 적용의 효과:** 초당 0.02초 간격의 간헐적 적용은 Baseline (워터마킹 미적용 상태) 대비 매우 미미하거나 낮은 수준의 오버헤드를 보였습니다.
    * **FHD (1920x1080):** LSB 0.08ms, DCT (Coeff 63) 0.09ms, DWT (Coeff 16) 0.08ms의 짧은 오버헤드.
    * **4K (3840x2160):** LSB 0.01ms 이하, DCT (Coeff 16) 0.02ms, DWT 0.02ms의 오버헤드.
    * **핵심 시사점:** 워터마킹 활성화 시간이 짧을수록 실시간 렌더링 성능 저하를 최소화할 수 있습니다.
* **매 프레임 적용의 한계:** 매 프레임 워터마킹 함수를 호출하는 경우, CPU 및 GPU 처리 속도가 급격히 느려지고 평균 FPS가 유의미하게 감소하는 현상을 관찰했습니다.
* **기법별 성능:** LSB가 가장 낮은 성능 부하를 보였으며, 다음으로 DWT, DCT 순이었습니다.
* **해상도 영향:** 4K 해상도에서는 동일 'Active Time'에서도 FHD 대비 더 높은 CPU/GPU 시간 및 낮은 FPS 값을 보였습니다.

### 이미지 화질 분석 (PSNR, SSIM, MSE)

* **워터마킹 강도의 영향:** LSB를 제외한 모든 기법에서 워터마킹 강도가 증가할수록 PSNR 및 SSIM 값은 감소하고 MSE 값은 증가하여 화질이 저하됨을 확인했습니다.
* **확산 스펙트럼 계수의 영향:** 확산 스펙트럼 계수가 증가할수록 같은 워터마킹 강도에서도 훨씬 화질이 저하됩니다.
* **LSB 화질:** PSNR 48.23 dB, SSIM 0.9989로 비교적 높은 화질을 유지했습니다. 하지만 공간 영역 기법 특성상 압축 공격에 취약하다는 한계가 있습니다.
* **DCT 및 DWT의 화질 vs. 강도 트레이드오프:** 워터마크의 견고성을 높이거나 더 많은 정보를 삽입하기 위해 강도를 높이거나 계수 개수를 늘리면 이미지 품질이 저하됩니다. 하지만 강도 조절 및 계수 선택을 통해 비가시성과 견고성 간의 균형을 보다 유연하게 조절할 수 있습니다.
    * PSNR 40dB 이상, SSIM 0.95 이상을 유지하기 위한 최소 강도 및 계수 조건이 존재함을 확인했습니다.

## 한계점 및 향후 연구

* **간단한 테스트 씬:** 본 연구는 워터마킹 연산 자체의 순수 성능 부하 측정을 위해 의도적으로 간단한 오브젝트와 기본적인 렌더링 설정으로 구성된 테스트 씬에서 진행되었습니다. 실제 상용 게임 환경에서는 측정된 성능 지표와 다소 차이가 발생할 수 있으며, 향후 다양한 환경에서의 추가 검증이 필요합니다.
* **견고성 분석 부재:** 본 연구는 워터마크의 삽입 빈도 및 강도에 따른 성능과 화질 분석에 초점을 맞췄으며, **삽입된 워터마크가 다양한 공격(압축, 필터링, 노이즈 등)에 얼마나 견고한지에 대한 평가는 수행하지 않았습니다.** 이는 실질적인 콘텐츠 보호 기술로서 워터마킹의 효용성을 평가하는 데 있어 가장 중요한 요소이므로, 향후 연구에서 반드시 다루어져야 할 부분입니다.
* **향후 연구 방향:**
    * 다양한 게임 콘텐츠 시나리오에서의 추가 실험.
    * 다른 변환 영역 기법(예: SVD, DCT-DWT 혼합 기법) 및 상업용 솔루션과의 비교.
    * 지각적 비가시성을 고려한 적응적 워터마킹 강도 조절 방안.
    * **다양한 공격에 대한 워터마크 견고성 분석.**

## 기여 (Contributors)

* 남시영 ([Si-young Nam](mailto:si.nam@hongik.ac.kr))
* 우재영 ([Jae-Yeong Woo](mailto:jae.woo@hongik.ac.kr))
* 박정민 ([Jung-Min Park](mailto:jung.park@hongik.ac.kr))
* 김혜영 ([Hye-Young Kim](mailto:hykim@hongik.ac.kr)) - Corresponding Author

## 라이선스

본 프로젝트의 코드는 **Creative Commons Attribution Non-Commercial 3.0 (CC BY-NC 3.0)** 라이선스에 따라 배포됩니다. 이는 비상업적인 용도로는 자유로운 사용, 배포, 복제, 그리고 2차적 저작물 작성을 허용하며, 원저작자(본 연구진)를 적절히 인용해야 합니다.

* **라이선스 전문 보기:** [http://creativecommons.org/licenses/by-nc/3.0/](http://creativecommons.org/licenses/by-nc/3.0/)

# Unity SRPベースのリアルタイムハイブリッド透かしの性能と画質分析

## プロジェクト概要

本プロジェクトは、Unity Universal Render Pipeline (URP)環境下におけるリアルタイムデジタル透かし技術の性能および画質への影響を分析するために開発されました。デジタルコンテンツの知的財産権保護と無断複製防止の重要性が高まる中、ゲームのようなリアルタイムインタラクティブコンテンツへの効果的な透かし適用方法を探求します。

## 研究目的

* Unity URP環境の最終レンダリング段階に、LSB (Least Significant Bit)、DCT (Discrete Cosine Transform)、DWT (Discrete Wavelet Transform)に基づく透かし技術を統合。
* コンピュートシェーダーを用いてGPU上でリアルタイム透かし埋め込み演算を実行。
* 透かし適用頻度（毎フレームおよび間欠的適用）と透かし強度（ウォーターマーク強度）が、レンダリング性能（フレームレート、CPU/GPU負荷）および画像画質（PSNR、SSIM、MSE）に与える影響を定量的に分析。
* 計算負荷と視覚的忠実度のバランスをとるリアルタイム透かし適用に関する基礎データを提供。

## 使用技術スタック

* **ゲームエンジン:** Unity 2022.3.60f1
* **レンダーパイプライン:** Universal Render Pipeline (URP)
* **開発ツール:** Visual Studio 2022
* **性能測定:** Unity Profiler (FPS, CPU Main Thread time, GPU Frame time)
* **画像画質測定:** Python (PSNR、SSIMにはScikit-image、MSEにはNumpy)
* **開発言語:** C#、HLSL (Compute Shaders)

## 主要機能と実装詳細

本プロジェクトでは、Unity URPレンダリングパイプライン内にカスタムレンダーパスとして透かし技術を実装しました。ポストプロセス後の最終レンダーターゲットに直接アクセスし、GPU上でリアルタイムに透かし埋め込み演算を実行します。

### 1. LSB (Least Significant Bit) 透かし

* 最終レンダリングされたテクスチャのピクセルカラーチャンネル(R, G, B)のうち、Blueチャンネルの最下位ビットを事前に定義された透かしビットシーケンスに置き換える方式で実装されています。
* **特徴:** 実装が単純で計算効率が高い。優れた不可視性を提供するが、画像圧縮、フィルタリング、ノイズ挿入などピクセル値を変更する攻撃に対しては根本的に脆弱です。

### 2. DCT (Discrete Cosine Transform) + スペクトル拡散透かし

* 最終レンダリングされたテクスチャ画像を8x8ブロックに分割し、各ブロックに2D DCT Type 2変換を適用します。
* 透かし埋め込みに使用される変換領域（AC係数）の係数に、秘密鍵に基づく疑似乱数（PN）シーケンスと透かし信号を結合して追加します。その後、逆DCT変換により空間領域に復元します。
* **特徴:** 画像圧縮標準（例：JPEG）で広く使用されており、中周波数帯域を利用して透かしの堅牢性を高めます。スペクトル拡散技術により堅牢性をさらに向上させます。

### 3. DWT (Discrete Wavelet Transform) + スペクトル拡散透かし

* 最終レンダリングされたテクスチャ画像に2D Haarウェーブレット変換を適用し、LL、LH、HL、HHなどのサブバンドに分解します。本実験では8x8ブロックを使用し、HHサブバンドにのみ透かし信号を追加して計算負荷を軽減しました。
* 透かし埋め込みに使用される詳細サブバンド（HH）の係数に、秘密鍵に基づくPNシーケンスと透かし信号を結合して追加します。その後、逆DWT変換により空間領域に復元します。
* **特徴:** 画像の空間的局所性を効果的に反映し、JPEG2000圧縮標準の基礎となります。スペクトル拡散技術により透かしの堅牢性を確保します。

## セットアップと使用方法

1.  **Unityのインストール:** Unity Hub経由でUnity 2022.3.60f1をインストールします。
2.  **プロジェクトのクローン:**
    ```bash
    git clone [PROJECT_GITHUB_URL]
    cd [PROJECT_FOLDER_NAME]
    ```
3.  **Unityプロジェクトを開く:** Unity Hubでクローンしたプロジェクトフォルダーを開きます。
4.  **シーンの実行:** `Assets/Scenes/`パスにある`[テストシーン名].unity`ファイルを開きます。
5.  **性能測定:** Unityエディターで`Window > Analysis > Profiler`メニューからプロファイラーウィンドウを開き、再生ボタンを押して性能を測定します。
6.  **画質測定:** 画質測定用のPythonスクリプト（例：`image_quality_analyzer.py`）は`Assets/Scripts/ImageQuality/`パスにあります。Unityで透かし適用前後の画像を保存した後、これらのスクリプトを使用してPSNR、SSIM、MSEの値を計算します。

## 主要実験結果

### 性能分析（FHDおよび4K解像度）

* **適用頻度の重要性:** 透かし適用頻度が増加するほど（すなわち、1秒あたりの透かし有効時間が長くなるほど）、CPU Main Thread時間およびGPU Frame時間が増加し、平均FPSは線形的に減少します。これはリアルタイムレンダリング性能に直接的な負荷をかけることを意味します。
* **間欠的適用の効果:** 1秒あたり0.02秒間隔の間欠的適用は、ベースライン（透かし未適用状態）と比較して非常にわずかまたは低いオーバーヘッドを示しました。
    * **FHD (1920x1080):** LSBは約0.08ms、DCT (Coeff 63)は約0.09ms、DWT (Coeff 16)は約0.08msの短いオーバーヘッド。
    * **4K (3840x2160):** LSBは0.01ms未満、DCT (Coeff 16)は約0.02ms、DWTは約0.02msのオーバーヘッド。
    * **主要な示唆:** 透かし有効時間を最小限に抑えることで、リアルタイムレンダリング性能の低下を大幅に軽減できます。
* **毎フレーム適用の限界:** 透かし関数が毎フレーム呼び出される場合、CPUおよびGPUの処理速度が急激に低下し、平均FPSが著しく減少する現象が観察されました。
* **技術別性能:** LSBが最も低い性能負荷を示し、DWT、DCTの順でした。
* **解像度の影響:** 4K解像度では、同じ有効時間でもFHDと比較してCPU/GPU時間が長く、FPS値が低い結果となりました。

### 画像画質分析（PSNR、SSIM、MSE）

* **透かし強度の影響:** LSBを除く全ての技術において、透かし強度が増加するにつれてPSNRおよびSSIM値が減少し、MSE値が増加し、画質が劣化することが確認されました。
* **スペクトル拡散係数の影響:** スペクトル拡散係数が増加するにつれて、同じ透かし強度でも画質が著しく劣化しました。
* **LSBの画質:** PSNRは48.23 dB、SSIMは0.9989で、比較的高い画質を維持しました。しかし、空間領域技術の特性上、圧縮攻撃には脆弱であるという限界があります。
* **DCTとDWTの画質対強度トレードオフ:** 透かしの堅牢性を高めたり、より多くの情報を埋め込んだりするために強度や係数の数を増やすと、画質が劣化します。しかし、これらの変換領域技術は、強度と係数調整を通じて不可視性と堅牢性のバランスをより柔軟に調整できます。
    * PSNR 40dB以上、SSIM 0.95以上を維持するための最小強度および係数条件が特定されました。

## 限界と今後の研究

* **簡易テストシーン:** 本研究は、透かし演算自体の純粋な性能負荷を測定し、外部変数を最小限に抑えるために意図的に単純なオブジェクトと基本的なレンダリング設定で構成されたテストシーンで実施されました。複雑なグラフィックス要素や様々な後処理効果が適用された実際の商用ゲーム環境では、本研究で測定された性能指標とは異なる結果が生じる可能性があり、今後の多様な環境での追加検証が必要です。
* **堅牢性分析の欠如:** 本研究は、透かしの挿入頻度と強度に基づく性能および画質分析に焦点を当てました。**しかし、埋め込まれた透かしが様々な攻撃（例：圧縮、フィルタリング、ノイズ）に対してどれほど堅牢であるかについての評価は実施されていません。** これは、実用的なコンテンツ保護技術として透かしの有用性を評価する上で最も重要な要素であるため、今後の研究で必ず取り組む必要があります。
* **今後の研究方向:**
    * 多様なゲームコンテンツシナリオにおける追加実験。
    * 他の変換領域技術（例：SVD、DCT-DWTハイブリッド手法）や既存の商用ソリューションとの比較。
    * 知覚的不可視性を考慮した適応型透かし強度調整。
    * **様々な攻撃に対する透かしの堅牢性分析。**

## 貢献者

* キム・ヘヨン (Hye-Young Kim) - 責任著者 (Corresponding Author) - hykim@hongik.ac.kr
* ナム・シヨン (Si-Young Nam)
* パク・ジョンミン (Jung-Min Park)
* ウ・ジェヨン (Jae-Yeong Woo)

## ライセンス

本プロジェクトのコードは、**Creative Commons Attribution Non-Commercial 3.0 (CC BY-NC 3.0)** ライセンスの下で配布されます。これにより、非営利目的での無制限の使用、配布、複製、派生作品の作成が許可され、元の作者（本研究者）は適切に引用される必要があります。

* **ライセンス全文を見る:** [http://creativecommons.org/licenses/by-nc/3.0/](http://creativecommons.org/licenses/by-nc/3.0/)

# Performance and Image Quality Analysis of Unity SRP-Based Real-time Hybrid Watermarking

## Project Overview

This project was developed to analyze the performance and image quality impact of real-time digital watermarking techniques within the Unity Universal Render Pipeline (URP) environment. As the importance of protecting intellectual property and preventing unauthorized copying of digital content grows, this research explores effective watermarking application methods for real-time interactive content, such as games.

## Research Objectives

* Integrate LSB (Least Significant Bit), DCT (Discrete Cosine Transform), and DWT (Discrete Wavelet Transform) based watermarking techniques into the final rendering stage of the Unity URP environment.
* Perform real-time watermark embedding operations on the GPU using compute shaders.
* Quantitatively analyze the impact of watermark application frequency (per-frame and intermittent application) and watermarking strength on rendering performance (frame rate, CPU/GPU load) and image quality (PSNR, SSIM, MSE).
* Provide foundational data for real-time watermarking application that balances computational load and visual fidelity.

## Technology Stack

* **Game Engine:** Unity 2022.3.60f1
* **Render Pipeline:** Universal Render Pipeline (URP)
* **Development Tools:** Visual Studio 2022
* **Performance Measurement:** Unity Profiler (FPS, CPU Main Thread time, GPU Frame time)
* **Image Quality Measurement:** Python (Scikit-image for PSNR, SSIM, NumPy for MSE)
* **Development Languages:** C#, HLSL (Compute Shaders)

## Key Features and Implementation Details

This project implements watermarking techniques as a Custom Render Pass within the Unity URP rendering pipeline. Watermark embedding operations are performed in real-time on the GPU by directly accessing the final render target after post-processing.

### 1. LSB (Least Significant Bit) Watermarking

* Implemented by replacing the least significant bit of the Blue color channel (R, G, B) of the final render texture's pixels with a predefined watermark bit sequence.
* **Characteristics:** Simple to implement and computationally efficient. Provides excellent imperceptibility but is fundamentally vulnerable to attacks that alter pixel values, such as image compression, filtering, or noise injection.

### 2. DCT (Discrete Cosine Transform) + Spread Spectrum Watermarking

* The final render texture image is divided into 8x8 blocks, and a 2D DCT Type 2 transform is applied to each block.
* A pseudo-random number (PN) sequence based on a secret key and the watermark signal are combined and added to the coefficients of the transform domain (AC coefficients) designated for watermark embedding. The image is then restored to the spatial domain via inverse DCT.
* **Characteristics:** Widely used in image compression standards (e.g., JPEG). Utilizes mid-frequency bands to enhance watermark robustness. Spread Spectrum technique further increases robustness.

### 3. DWT (Discrete Wavelet Transform) + Spread Spectrum Watermarking

* A 2D Haar wavelet transform is applied to the final render texture image, decomposing it into sub-bands such as LL, LH, HL, and HH. In this experiment, 8x8 blocks were used, and the watermark signal was added only to the HH sub-band to reduce computational load.
* A secret key-based PN sequence and the watermark signal are combined and added to the detail sub-band (HH) coefficients used for watermark embedding. The image is then restored to the spatial domain via inverse DWT.
* **Characteristics:** Effectively reflects the spatial locality of an image and forms the basis of the JPEG2000 compression standard. The Spread Spectrum technique ensures watermark robustness.

## Setup and Usage

1.  **Install Unity:** Install Unity 2022.3.60f1 via Unity Hub.
2.  **Clone the Project:**
    ```bash
    git clone [PROJECT_GITHUB_URL]
    cd [PROJECT_FOLDER_NAME]
    ```
3.  **Open Unity Project:** Open the cloned project folder in Unity Hub.
4.  **Run Scene:** Open the `[TEST_SCENE_NAME].unity` file located in `Assets/Scenes/`.
5.  **Measure Performance:** Open the Profiler window via `Window > Analysis > Profiler` in the Unity editor, then press the play button to measure performance.
6.  **Measure Image Quality:** Python scripts for image quality measurement (e.g., `image_quality_analyzer.py`) are located in `Assets/Scripts/ImageQuality/`. After saving pre- and post-watermarking images from Unity, use these scripts to calculate PSNR, SSIM, and MSE values.

## Key Experimental Results

### Performance Analysis (FHD and 4K Resolution)

* **Importance of Application Frequency:** As the watermark application frequency increases (i.e., longer active watermarking time per second), CPU Main Thread time and GPU Frame time increase, while average FPS linearly decreases. This indicates a direct load on real-time rendering performance.
* **Effect of Intermittent Application:** Intermittent application at 0.02 seconds per second showed very minimal or low overhead compared to the Baseline (no watermarking).
    * **FHD (1920x1080):** LSB ~0.08ms, DCT (Coeff 63) ~0.09ms, DWT (Coeff 16) ~0.08ms of short overhead.
    * **4K (3840x2160):** LSB <0.01ms, DCT (Coeff 16) ~0.02ms, DWT ~0.02ms of overhead.
    * **Key Implication:** Minimizing the active watermarking time can significantly reduce real-time rendering performance degradation.
* **Limitations of Per-Frame Application:** When the watermarking function was called every frame, CPU and GPU processing speeds drastically slowed down, and average FPS significantly decreased.
* **Technique Performance:** LSB showed the lowest performance overhead, followed by DWT, then DCT.
* **Resolution Impact:** At 4K resolution, even with the same active time, higher CPU/GPU times and lower FPS values were observed compared to FHD.

### Image Quality Analysis (PSNR, SSIM, MSE)

* **Impact of Watermarking Strength:** For all techniques except LSB, increasing watermarking strength resulted in decreased PSNR and SSIM values and increased MSE values, indicating image quality degradation.
* **Impact of Spread Spectrum Coefficient:** Increasing the Spread Spectrum coefficient led to significantly worse image quality at the same watermarking strength.
* **LSB Image Quality:** PSNR of 48.23 dB and SSIM of 0.9989, indicating relatively high image quality preservation. However, as a spatial domain technique, it is vulnerable to compression attacks.
* **DCT and DWT Quality vs. Strength Trade-off:** Increasing strength or the number of coefficients to enhance watermark robustness or embed more information degrades image quality. However, these transform domain techniques allow for more flexible balancing of imperceptibility and robustness through strength and coefficient adjustment.
    * Minimum strength and coefficient conditions were identified to maintain PSNR above 40dB and SSIM above 0.95.

## Limitations and Future Work

* **Simplified Test Scene:** This study utilized a intentionally simplified test scene with basic objects and rendering settings to measure the pure performance overhead of watermarking operations. Actual commercial game environments with complex graphics and various post-processing effects may show different performance metrics, requiring further validation in diverse environments.
* **Lack of Robustness Analysis:** This study focused on analyzing performance and image quality based on watermark insertion frequency and strength. **However, an evaluation of how robust the embedded watermarks are against various attacks (e.g., compression, filtering, noise) was not conducted.** This is a crucial factor for assessing the utility of watermarking as a practical content protection technology and must be addressed in future research.
* **Future Research Directions:**
    * Additional experiments in diverse game content scenarios.
    * Comparison with other transform domain techniques (e.g., SVD, DCT-DWT hybrid methods) and existing commercial solutions.
    * Adaptive watermarking strength adjustment based on perceptual imperceptibility.
    * **Robustness analysis of watermarks against various attacks.**

## Contributors

* Hye-Young Kim - Corresponding Author - hykim@hongik.ac.kr
* Si-Young Nam
* Jung-Min Park
* Jae-Yeong Woo

## License

This project's code is distributed under the **Creative Commons Attribution Non-Commercial 3.0 (CC BY-NC 3.0)** License. This permits unrestricted non-commercial use, distribution, reproduction, and creation of derivative works, provided the original author(s) are properly cited.

* **View Full License:** [http://creativecommons.org/licenses/by-nc/3.0/](http://creativecommons.org/licenses/by-nc/3.0/)
