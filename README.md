# RDP Keyboard Translator

這個工具解決了Android RDP軟鍵盤與Warp Terminal（以及VS Code Claude Code擴展）的相容性問題。

## 問題描述

- **Warp Terminal**為了性能優化，只監聽低階硬體掃描碼(Hardware Scancodes)
- **Android RDP軟鍵盤**發送高階軟體事件到Windows
- **結果**：Warp Terminal無法識別RDP軟鍵盤輸入

## 解決方案

本轉譯器作為中間層，攔截RDP軟鍵盤事件並轉換為硬體掃描碼事件。

### 工作原理

```
Android RDP軟鍵盤 → Windows高階事件 → [轉譯器] → 硬體掃描碼 → Warp Terminal
```

## 系統要求

- **作業系統**: Windows 10/11
- **權限**: 管理員權限（需要全域鍵盤Hook）
- **.NET**: .NET 6.0 或更高版本

## 功能特色

- ✅ **TSF架構**: 基於Text Services Framework的現代化設計
- ✅ **虛擬剪貼簿隔離**: 完全不影響系統剪貼簿
- ✅ **智能字符處理**: ASCII使用PostMessage，Unicode使用隔離剪貼簿
- ✅ **系統托盤界面**: 用戶友好的圖形界面
- ✅ **優化窗口檢測**: 智能緩存和應用程式特定處理

## 安裝與使用

### 1. 建置應用程式

```cmd
build.bat
```

### 2. 執行轉譯器

**🌟 推薦方式：系統托盤模式**
```cmd
# 直接執行（默認托盤模式）
RdpKeyboardTranslator.exe

# 或使用腳本
run_admin.bat
```

**🔧 調試模式：控制台輸出**
```cmd
# 顯示詳細調試信息
RdpKeyboardTranslator.exe --console

# 或使用腳本
run.bat
```

### 3. 使用說明

#### 系統托盤模式（默認，推薦）
1. 以管理員身份運行 `run_admin.bat`
2. 選擇 "1" 進入托盤模式
3. 應用程式將在系統托盤中顯示 "R" 圖標
4. 右鍵點擊托盤圖標可訪問選項菜單
5. 使用Android裝置連接到Windows的RDP
6. 現在可以在Warp Terminal中正常使用軟鍵盤

#### 控制台模式（調試用）
1. 啟動後會顯示詳細的調試信息
2. **退出轉譯器**: 同時按下 `ESC + F12`

#### 托盤圖標菜單選項
- **啟用/停用轉換器**: 快速切換功能
- **顯示狀態**: 查看當前運行狀態
- **調試模式**: 顯示控制台窗口
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
├─ 智能字符路由
│  ├─ ASCII (32-126): PostMessage WM_CHAR (100%可靠)
│  └─ Unicode (中文等): VirtualClipboardService
│
├─ 虛擬剪貼簿隔離服務
│  ├─ 專用隱藏窗口 (RdpTranslatorVirtualClip_[PID])
│  ├─ 系統剪貼簿備份與恢復
│  └─ Ctrl+V注入機制
│
└─ 優化窗口檢測
   ├─ 智能緩存 (500ms超時)
   ├─ 應用程式特定處理 (Chrome/Terminal/Native)
   └─ 系統窗口過濾
```

### 核心功能

- **VK_PACKET檢測**: 完美攔截RDP軟鍵盤事件（VK_PACKET消息）
- **TSF統一入口**: 所有文字輸入通過TSF框架處理
- **字符分流處理**: 根據字符類型自動選擇最優方法
- **虛擬剪貼簿隔離**: 完全不影響用戶的系統剪貼簿
- **優化窗口檢測**: 智能識別和緩存目標應用程式窗口
- **系統托盤界面**: 用戶友好的圖形控制界面

### 支援範圍

- ✅ **完整ASCII字符**: A-Z, 0-9, 標點符號等 (PostMessage)
- ✅ **完整Unicode字符**: 中文、日文、韓文等 (虛擬剪貼簿)
- ✅ **目標應用程式**: Warp Terminal, VS Code, Chrome, 記事本等
- ✅ **RDP環境**: Android/iOS RDP客戶端軟鍵盤

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
1. 確認轉譯器正在運行（應該看到控制台視窗）
2. 確認是從Android RDP軟鍵盤輸入，不是實體鍵盤
3. 嘗試重啟轉譯器

**Q: 實體鍵盤不工作？**
A: 轉譯器設計為不影響實體鍵盤。如有問題，請重啟轉譯器。

**Q: 如何確認轉譯器正在工作？**
A: 轉譯器會在控制台顯示狀態訊息。成功時，RDP軟鍵盤應該能在Warp Terminal中正常工作。

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