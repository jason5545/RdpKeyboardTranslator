# RDP Keyboard Translator

這個工具解決了Android RDP軟體鍵盤與Warp Terminal（以及VS Code Claude Code擴展）的相容性問題。

## 問題描述

- **Warp Terminal**為了性能最佳化，只監聽低階硬體掃描碼(Hardware Scancodes)
- **Android RDP軟體鍵盤**發送高階軟體事件到Windows
- **結果**：Warp Terminal無法識別RDP軟體鍵盤輸入

## 解決方案

本轉譯器作為中間層，攔截RDP軟體鍵盤事件並轉換為硬體掃描碼事件。

### 工作原理

```
Android RDP軟體鍵盤 → Windows高階事件 → [轉譯器] → 硬體掃描碼 → Warp Terminal
```

## 系統要求

- **作業系統**: Windows 10/11
- **權限**: 管理員權限（需要全域鍵盤Hook）
- **.NET**: .NET 6.0 或更高版本

## 功能特色

- ✅ **TSF架構**: 基於Text Services Framework的現代化設計
- ✅ **虛擬剪貼簿隔離**: 完全不影響系統剪貼簿
- ✅ **智慧字符處理**: ASCII使用PostMessage，Unicode使用隔離剪貼簿
- ✅ **系統匣界面**: 用戶友好的圖形界面
- ✅ **最佳化視窗偵測**: 智慧快取和應用程式特定處理

## 安裝與使用

### 1. 建置應用程式

```cmd
build.bat
```

### 2. 執行轉譯器

**🌟 推薦方式：系統匣模式**
```cmd
# 直接執行（默認系統匣模式）
RdpKeyboardTranslator.exe

# 或使用腳本
run_admin.bat
```

**🔧 偵錯模式：主控台輸出**
```cmd
# 顯示詳細偵錯資訊
RdpKeyboardTranslator.exe --console

# 或使用腳本
run.bat
```

**⚡ Windows服務模式：持續執行**
```cmd
# 安裝為Windows服務（需要管理員權限）
install_service.bat

# 管理服務
sc start RdpKeyboardTranslator    # 啟動服務
sc stop RdpKeyboardTranslator     # 停止服務
service_status.bat                # 檢查服務狀態
restart_service.bat               # 重啟服務

# 卸載服務
uninstall_service.bat
```

### 3. 使用說明

#### 系統匣模式（默認，推薦）
1. 以管理員身份執行 `run_admin.bat`
2. 選擇 "1" 進入系統匣模式
3. 應用程式將在系統匣中顯示 "R" 圖標
4. 右鍵點擊系統匣圖標可訪問選項菜單
5. 使用Android裝置連接到Windows的RDP
6. 現在可以在Warp Terminal中正常使用軟體鍵盤

#### 主控台模式（偵錯用）
1. 啟動後會顯示詳細的偵錯資訊
2. **退出轉譯器**: 同時按下 `ESC + F12`

#### Windows服務模式（推薦：持續執行）
1. 以管理員身份執行 `install_service.bat`
2. 服務將自動啟動並設為開機自啟
3. 使用 `service_status.bat` 檢查服務狀態
4. 服務在背景持續執行，無需用戶介入
5. 系統重啟後自動恢復執行

#### 系統匣圖標菜單選項
- **啟用/停用轉換器**: 快速切換功能
- **顯示狀態**: 查看當前執行狀態
- **偵錯模式**: 顯示主控台視窗
- **關於**: 查看版本信息
- **退出**: 完全關閉程式

## 技術架構 v2.0

### 統一TSF架構
```
┌─ TSF Framework (Text Services Framework)
│  ├─ ITfThreadMgr: 線程管理
│  ├─ ITfDocumentMgr: 文檔管理  
│  └─ ITfContext: 上下文管理
│
├─ 智慧字符路由
│  ├─ ASCII (32-126): PostMessage WM_CHAR (100%可靠)
│  └─ Unicode (中文等): VirtualClipboardService
│
├─ 虛擬剪貼簿隔離服務
│  ├─ 專用隱藏視窗 (RdpTranslatorVirtualClip_[PID])
│  ├─ 系統剪貼簿備份與恢復
│  └─ Ctrl+V注入機制
│
└─ 最佳化視窗偵測
   ├─ 智慧快取 (500ms超時)
   ├─ 應用程式特定處理 (Chrome/Terminal/Native)
   └─ 系統視窗過濾
```

### 核心功能

- **VK_PACKET偵測**: 完美攔截RDP軟體鍵盤事件（VK_PACKET訊息）
- **TSF統一入口**: 所有文字輸入通過TSF框架處理
- **字符分流處理**: 根據字符類型自動選擇最優方法
- **虛擬剪貼簿隔離**: 完全不影響用戶的系統剪貼簿
- **最佳化視窗偵測**: 智慧識別和快取目標應用程式視窗
- **系統匣界面**: 用戶友好的圖形控制界面

### 支援範圍

- ✅ **完整ASCII字符**: A-Z, 0-9, 標點符號等 (PostMessage)
- ✅ **完整Unicode字符**: 中文、日文、韓文等 (虛擬剪貼簿)
- ✅ **目標應用程式**: Warp Terminal, VS Code, Chrome, 記事本等
- ✅ **RDP環境**: Android/iOS RDP客戶端軟體鍵盤

### 安全與穩定性

- **完全隔離**: 虛擬剪貼簿服務與系統剪貼簿完全獨立
- **資源管理**: 完整的TSF資源初始化和清理機制
- **異常安全**: 多層級錯誤處理和自動恢復
- **無數據洩露**: 不記錄或儲存任何用戶輸入
- **零副作用**: 不影響實體鍵盤和其他應用程式

## 故障排除

### 常見問題

**Q: 轉譯器無法啟動？**
A: 確保以管理員權限執行，全域鍵盤Hook需要管理員權限。

**Q: Warp Terminal仍然無法識別輸入？**
A: 
1. 確認轉譯器正在執行（應該看到控制台視窗）
2. 確認是從Android RDP軟體鍵盤輸入，不是實體鍵盤
3. 嘗試重啟轉譯器

**Q: 實體鍵盤不工作？**
A: 轉譯器設計為不影響實體鍵盤。如有問題，請重啟轉譯器。

**Q: 如何確認轉譯器正在工作？**
A: 轉譯器會在控制台顯示狀態訊息。成功時，RDP軟體鍵盤應該能在Warp Terminal中正常工作。

## 開發資訊

### 檔案結構

- `RdpKeyboardTranslator.cs` - 主要程式碼
- `RdpKeyboardTranslator.csproj` - .NET專案檔
- `app.manifest` - 管理員權限配置
- `build.bat` - 建置腳本
- `run.bat` / `run_admin.bat` - 執行腳本

### 編譯

需要 .NET 6.0 SDK:
```cmd
dotnet build --configuration Release
```

## 授權

本專案為開源軟體，用於解決Warp Terminal在RDP環境下的相容性問題。