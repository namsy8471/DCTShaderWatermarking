import sys
import os
import numpy as np
from PIL import Image, UnidentifiedImageError
from skimage.metrics import peak_signal_noise_ratio as psnr
from skimage.metrics import structural_similarity as ssim

import traceback
import datetime

# PyQt5 imports
from PyQt5.QtWidgets import (QApplication, QWidget, QVBoxLayout, QGridLayout, QLabel,
                             QLineEdit, QPushButton, QProgressBar, QTextEdit, QStatusBar,
                             QFileDialog, QMessageBox)
from PyQt5.QtCore import QThread, pyqtSignal
from PyQt5.QtGui import QFont


try:
    import OpenEXR
    import Imath
    exr_support = True
except ImportError:
    exr_support = False
    print("경고: OpenEXR 라이브러리를 찾을 수 없습니다. EXR 파일 지원이 제한될 수 있습니다.")

# --- 화질 비교 계산을 위한 Worker Thread ---
class CalculationThread(QThread):
    progress_update = pyqtSignal(int)
    calculation_complete = pyqtSignal(float, float, float)
    error_occurred = pyqtSignal(str)
    log_message = pyqtSignal(str)

    def __init__(self, original_path, watermarked_path):
        super().__init__()
        self.original_path = original_path
        self.watermarked_path = watermarked_path

    def load_exr_manual(self, filepath):
        if not exr_support: raise ImportError("EXR load requires OpenEXR.")
        try:
            exr_file = OpenEXR.InputFile(filepath)
            header = exr_file.header()
            dw = header['dataWindow']; size = (dw.max.x - dw.min.x + 1, dw.max.y - dw.min.y + 1)
            ch_info = header['channels']; ch_names = list(ch_info.keys())
            
            pixel_type_str = str(ch_info[ch_names[0]].type) 
            if "HALF" in pixel_type_str.upper():
                dtype_np = np.float16
            elif "FLOAT" in pixel_type_str.upper():
                dtype_np = np.float32
            else: 
                dtype_np = np.float32

            if 'R' in ch_names and 'G' in ch_names and 'B' in ch_names:
                channels_to_load = ['R', 'G', 'B']
            elif 'Y' in ch_names:
                channels_to_load = ['Y']
            else: 
                if ch_names: channels_to_load = [ch_names[0]]
                else: raise ValueError("No channels found in EXR file.")
            
            buffers = exr_file.channels(channels_to_load, pixel_type=ch_info[channels_to_load[0]].type)
            img_data = [np.frombuffer(buf, dtype=dtype_np).reshape(size[1], size[0]) for buf in buffers]
            exr_file.close()

            if len(img_data) == 3: # RGB
                rgb = np.stack(img_data, axis=-1).astype(np.float64)
                return 0.2126 * rgb[:,:,0] + 0.7152 * rgb[:,:,1] + 0.0722 * rgb[:,:,2]
            return img_data[0].astype(np.float64)
        except Exception as e:
            traceback.print_exc()
            raise

    def load_image_data(self, filepath):
        img_arr = None; is_source_float = False
        try:
            is_exr = filepath.lower().endswith('.exr')
            if is_exr:
                is_source_float = True
                try: 
                    img = Image.open(filepath)
                    if img.mode in ['F', 'I']: 
                        img_arr_maybe = np.array(img)
                        if img_arr_maybe.dtype.kind == 'f': img_arr = img_arr_maybe
                        else: img_arr = self.load_exr_manual(filepath) 
                    elif img.mode in ['RGBF', 'RGBAF']: 
                        img_arr_color = np.array(img)
                        img_arr = 0.2126*img_arr_color[:,:,0] + 0.7152*img_arr_color[:,:,1] + 0.0722*img_arr_color[:,:,2]
                    else: 
                        img_arr = self.load_exr_manual(filepath)
                except (UnidentifiedImageError, AttributeError, ValueError, TypeError, OSError): 
                    img_arr = self.load_exr_manual(filepath)
            else: 
                img = Image.open(filepath)
                if img.mode != 'L': img = img.convert('L')
                img_arr = np.array(img)

            if img_arr is None: raise ValueError("Image data is None after loading attempts.")
            if img_arr.ndim == 3 and img_arr.shape[2] == 1: img_arr = img_arr.squeeze(axis=2)
            if img_arr.ndim != 2: raise ValueError(f"Final image is not 2D grayscale. Shape: {img_arr.shape}")
            
            return img_arr.astype(np.float64), is_source_float
        except Exception as e:
            traceback.print_exc()
            return None, False

    def run(self):
        try:
            self.progress_update.emit(10)
            arr_orig, is_orig_float_source = self.load_image_data(self.original_path)
            if arr_orig is None: self.error_occurred.emit(f"오류: 원본 로드 실패 ({os.path.basename(self.original_path)})"); return
            self.progress_update.emit(25)
            
            arr_wm, _ = self.load_image_data(self.watermarked_path)
            if arr_wm is None: self.error_occurred.emit(f"오류: 비교 대상 로드 실패 ({os.path.basename(self.watermarked_path)})"); return
            self.progress_update.emit(40)

            if arr_orig.shape != arr_wm.shape:
                self.error_occurred.emit(f"오류: 이미지 크기 불일치. 원본 {arr_orig.shape}, 비교대상 {arr_wm.shape}"); return

            data_range_for_metrics = 0
            if is_orig_float_source:
                min_val, max_val = np.min(arr_orig), np.max(arr_orig)
                data_range_for_metrics = max_val - min_val
                if data_range_for_metrics < 1e-9 : 
                    data_range_for_metrics = max(data_range_for_metrics, 1.0) 
            else: 
                data_range_for_metrics = 255.0
            
            if data_range_for_metrics == 0: 
                if not np.array_equal(arr_orig, arr_wm):
                    data_range_for_metrics = 255.0 

            mse_val = np.mean((arr_orig - arr_wm) ** 2); self.progress_update.emit(60)
            
            psnr_val = 0.0
            if mse_val < 1e-12: 
                psnr_val = float('inf')
            elif data_range_for_metrics < 1e-9:
                psnr_val = -float('inf') 
            else:
                psnr_val = psnr(arr_orig, arr_wm, data_range=data_range_for_metrics)
            self.progress_update.emit(80)
            
            ssim_val = 0.0
            min_dim = min(arr_orig.shape)
            win_size = max(3, min(7, min_dim if min_dim % 2 != 0 else min_dim - 1))

            if min_dim >= win_size : 
                ssim_val = ssim(arr_orig, arr_wm, data_range=data_range_for_metrics, win_size=win_size, channel_axis=None, gaussian_weights=True)
            else:
                ssim_val = 0.0 
                self.log_message.emit(f"참고: SSIM 계산을 위한 이미지 크기가 너무 작습니다 (min_dim: {min_dim}, win_size: {win_size}). SSIM은 0으로 처리됩니다.")

            self.progress_update.emit(100); self.calculation_complete.emit(psnr_val, ssim_val, mse_val)
        except Exception as e:
            traceback.print_exc()
            self.error_occurred.emit(f"계산 스레드 오류: {e}")


# --- 메인 GUI 애플리케이션 (ImageComparatorApp 클래스) ---
class ImageComparatorApp(QWidget):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("이미지 품질 비교 및 결과 저장 도구")
        self.setGeometry(100, 100, 700, 550) # 창 크기 조정

        self.calc_thread = None

        self.original_file_path = QLineEdit(self)
        self.watermarked_file_path = QLineEdit(self)
        self.save_dir_path = QLineEdit(self) # 결과 저장 폴더 경로 입력란

        self.init_ui()
        self.apply_styles()

    def init_ui(self):
        main_layout = QVBoxLayout(self)
        
        self.comparison_tab = QWidget()
        self._create_comparison_tab_ui()
        main_layout.addWidget(self.comparison_tab)
        
        self.status_bar = QStatusBar(self)
        main_layout.addWidget(self.status_bar)
        self.status_bar.showMessage("준비됨")

    def _create_comparison_tab_ui(self):
        layout = QGridLayout(self.comparison_tab)
        
        row_idx = 0
        layout.addWidget(QLabel("원본 이미지:", self.comparison_tab), row_idx, 0)
        self.original_file_path.setPlaceholderText("원본 이미지 파일 경로")
        layout.addWidget(self.original_file_path, row_idx, 1)
        btn_select_orig = QPushButton("파일 선택", self.comparison_tab)
        btn_select_orig.clicked.connect(lambda: self.select_file(self.original_file_path, "원본 이미지 선택"))
        layout.addWidget(btn_select_orig, row_idx, 2)
        row_idx += 1
        
        layout.addWidget(QLabel("비교 대상 이미지:", self.comparison_tab), row_idx, 0)
        self.watermarked_file_path.setPlaceholderText("비교 이미지 파일 경로")
        layout.addWidget(self.watermarked_file_path, row_idx, 1)
        btn_select_wm = QPushButton("파일 선택", self.comparison_tab)
        btn_select_wm.clicked.connect(lambda: self.select_file(self.watermarked_file_path, "비교 대상 이미지 선택"))
        layout.addWidget(btn_select_wm, row_idx, 2)
        row_idx += 1

        # 결과 저장 폴더 선택 UI 추가
        layout.addWidget(QLabel("결과 저장 폴더:", self.comparison_tab), row_idx, 0)
        self.save_dir_path.setPlaceholderText("결과를 저장할 폴더 경로 (선택 사항)")
        layout.addWidget(self.save_dir_path, row_idx, 1)
        btn_select_save_dir = QPushButton("폴더 선택", self.comparison_tab)
        btn_select_save_dir.clicked.connect(self.select_save_directory)
        layout.addWidget(btn_select_save_dir, row_idx, 2)
        row_idx += 1
        
        self.btn_start_comparison = QPushButton("품질 비교 시작", self.comparison_tab)
        self.btn_start_comparison.clicked.connect(self.start_comparison)
        layout.addWidget(self.btn_start_comparison, row_idx, 0, 1, 3)
        row_idx += 1
        
        self.progress_bar_compare = QProgressBar(self.comparison_tab)
        layout.addWidget(self.progress_bar_compare, row_idx, 0, 1, 3)
        row_idx += 1
        
        self.results_label_compare = QLabel("결과:", self.comparison_tab)
        layout.addWidget(self.results_label_compare, row_idx, 0)
        row_idx += 1
        
        self.results_text_compare = QTextEdit(self.comparison_tab)
        self.results_text_compare.setReadOnly(True)
        layout.addWidget(self.results_text_compare, row_idx, 0, 1, 3)
        
        layout.setRowStretch(row_idx, 1)

    def apply_styles(self):
        self.setStyleSheet("""
            QWidget { font-size: 10pt; }
            QPushButton { padding: 8px; background-color: #007bff; color: white; border-radius: 4px; }
            QPushButton:hover { background-color: #0056b3; }
            QPushButton:disabled { background-color: #cccccc; color: #666666; }
            QLineEdit, QTextEdit { border: 1px solid #ced4da; border-radius: 4px; padding: 5px; }
            QProgressBar { text-align: center; }
            QStatusBar { font-size: 9pt; }
            QLabel { margin-bottom: 2px; }
        """)

    def select_file(self, line_edit_widget, caption, file_filter="이미지 파일 (*.png *.jpg *.jpeg *.bmp *.tif *.tiff *.exr);;모든 파일 (*.*)"):
        file_path, _ = QFileDialog.getOpenFileName(self, caption, "", file_filter)
        if file_path:
            line_edit_widget.setText(file_path)

    def select_save_directory(self):
        dir_path = QFileDialog.getExistingDirectory(self, "결과를 저장할 폴더 선택")
        if dir_path:
            self.save_dir_path.setText(dir_path)

    def check_comparison_files_selected(self):
        if not self.original_file_path.text() or not self.watermarked_file_path.text():
            self.show_error("오류", "원본 이미지와 비교 대상 이미지를 모두 선택해주세요.")
            return False
        if not os.path.exists(self.original_file_path.text()):
            self.show_error("오류", f"원본 이미지 파일을 찾을 수 없습니다: {self.original_file_path.text()}")
            return False
        if not os.path.exists(self.watermarked_file_path.text()):
            self.show_error("오류", f"비교 대상 이미지 파일을 찾을 수 없습니다: {self.watermarked_file_path.text()}")
            return False
        
        save_dir = self.save_dir_path.text()
        if save_dir and not os.path.isdir(save_dir):
            self.show_error("오류", f"결과 저장 폴더가 유효하지 않습니다: {save_dir}")
            return False
            
        return True

    def start_comparison(self):
        if not self.check_comparison_files_selected():
            return
        
        if self.calc_thread and self.calc_thread.isRunning():
            self.show_error("알림", "이미 품질 비교가 진행 중입니다.")
            return

        self.btn_start_comparison.setEnabled(False)
        self.status_bar.showMessage("품질 비교 중...")
        self.progress_bar_compare.setValue(0)
        self.results_text_compare.clear()

        orig_path = self.original_file_path.text()
        wm_path = self.watermarked_file_path.text()
        
        self.calc_thread = CalculationThread(orig_path, wm_path)
        self.calc_thread.progress_update.connect(self.update_progress_compare)
        self.calc_thread.calculation_complete.connect(self.on_calculation_finished)
        self.calc_thread.error_occurred.connect(self.display_comparison_error)
        self.calc_thread.log_message.connect(self.log_calculation_message)
        self.calc_thread.finished.connect(lambda: self.btn_start_comparison.setEnabled(True))
        self.calc_thread.start()

    def update_progress_compare(self, value):
        self.progress_bar_compare.setValue(value)

    def display_comparison_error(self, message):
        self.show_error("계산 오류", message)
        self.status_bar.showMessage("오류 발생", 5000)
        self.results_text_compare.append(f"오류: {message}")

    def log_calculation_message(self, message):
        current_text = self.results_text_compare.toPlainText()
        if current_text and not current_text.endswith("\n\n" + message) and not current_text.endswith(message) :
             self.results_text_compare.append(f"\n{message}")
        elif not current_text:
             self.results_text_compare.append(message)


    def on_calculation_finished(self, psnr_val, ssim_val, mse_val):
        self.progress_bar_compare.setValue(100)
        self.status_bar.showMessage("품질 비교 완료", 5000)
        
        result_lines = [
            f"PSNR: {psnr_val:.4f} dB",
            f"SSIM: {ssim_val:.6f}",
            f"MSE: {mse_val:.4f}"
        ]
        result_str_ui = "\n".join(result_lines)
        
        current_ui_text = self.results_text_compare.toPlainText()
        if current_ui_text and not current_ui_text.strip().endswith(result_str_ui.strip()):
            self.results_text_compare.append("\n\n" + result_str_ui)
        elif not current_ui_text:
             self.results_text_compare.setText(result_str_ui)

        QMessageBox.information(self, "계산 완료", result_str_ui)

        # 결과 파일 저장 로직
        save_dir = self.save_dir_path.text()
        if save_dir and os.path.isdir(save_dir):
            try:
                orig_file_full_path = self.original_file_path.text()
                comp_file_full_path = self.watermarked_file_path.text()

                original_basename = os.path.splitext(os.path.basename(orig_file_full_path))[0]
                compared_basename = os.path.splitext(os.path.basename(comp_file_full_path))[0]
                timestamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
                
                filename = f"QualityReport_{original_basename}_vs_{compared_basename}_{timestamp}.txt"
                filepath = os.path.join(save_dir, filename)
                
                report_content = [
                    "Image Quality Comparison Report",
                    f"Date: {datetime.datetime.now().strftime('%Y-%m-%d %H:%M:%S')}",
                    "",
                    f"Original Image: {orig_file_full_path}",
                    f"Compared Image: {comp_file_full_path}",
                    "",
                    "Results:"
                ]
                report_content.extend(result_lines) # PSNR, SSIM, MSE 값들

                # SSIM 관련 로그 메시지가 있다면 파일에도 추가
                ssim_log_message = ""
                if "참고: SSIM 계산을 위한 이미지 크기가 너무 작습니다" in self.results_text_compare.toPlainText():
                     # UI 텍스트에서 해당 메시지만 추출 (좀 더 정확하게 하려면 CalculationThread에서 직접 전달받는 것이 좋음)
                    for line in self.results_text_compare.toPlainText().split('\n'):
                        if "참고: SSIM 계산을 위한 이미지 크기가 너무 작습니다" in line:
                            ssim_log_message = line
                            break
                if ssim_log_message:
                     report_content.append("") # 빈 줄 추가
                     report_content.append(ssim_log_message)


                with open(filepath, 'w', encoding='utf-8') as f:
                    f.write("\n".join(report_content))
                
                self.status_bar.showMessage(f"결과 저장됨: {filepath}", 7000)
                QMessageBox.information(self, "저장 완료", f"결과가 다음 파일에 저장되었습니다:\n{filepath}")
            except IOError as e:
                self.show_error("파일 저장 오류", f"결과를 파일에 저장하는 중 오류가 발생했습니다:\n{e}")
                self.status_bar.showMessage("파일 저장 오류", 5000)
            except Exception as e:
                self.show_error("오류", f"결과 저장 중 예기치 않은 오류 발생:\n{e}")
                self.status_bar.showMessage("결과 저장 중 오류", 5000)
        elif save_dir and not os.path.isdir(save_dir):
            # 이 경우는 check_comparison_files_selected에서 이미 처리되었어야 함
            self.status_bar.showMessage("결과 저장 폴더가 유효하지 않아 저장하지 않음.", 5000)


    def show_error(self, title, message):
        QMessageBox.critical(self, title, message)

if __name__ == '__main__':
    app = QApplication(sys.argv)
    try:
        font_set = False
        for font_name_try in ["Malgun Gothic", "AppleSDGothicNeo", "NanumGothic", "Arial", "sans-serif"]:
            font = QFont(font_name_try, 10)
            test_font = QFont(font_name_try)
            if QFont(font_name_try).exactMatch() or font.family().lower() == font_name_try.lower():
                app.setFont(font)
                print(f"Application font set to: {font_name_try}")
                font_set = True; break
        if not font_set: print("Warning: Preferred fonts not found. Using system default.")
    except Exception as e: print(f"Font setting error: {e}")

    window = ImageComparatorApp()
    window.show()
    sys.exit(app.exec_())