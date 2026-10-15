<#
.SYNOPSIS
  给游戏窗口发一次按键（默认 F7），用于无人值守地验证脚本/引擎的热键路径。

.DESCRIPTION
  为什么需要它：`Input.GetKeyDown` 只有在窗口收到真实按键时才会为真，而无人值守的会话里没人能用手按。
  做法分三步：
    1. 找窗口：**只认 Unity 的主窗口**（类名 `UnityWndClass`）—— 这一点很关键：
       `Process.MainWindowHandle` 会返回 BepInEx 的控制台窗口（`ConsoleWindowClass`），
       把按键发到控制台上，游戏那边永远不会响（`Application.isFocused` 一直是 False）。
    2. 抢前台 + 抢键盘焦点：先发一次 ALT（系统会因此解锁前台切换限制），
       再 AttachThreadInput + BringWindowToTop + SetActiveWindow + SetForegroundWindow + SetFocus，
       并轮询 GetForegroundWindow 确认真的抢到了。
    3. 发按键：抢到前台就用 keybd_event（系统级键盘状态，跟游戏怎么读输入无关）；
       抢不到就退而用 PostMessage 直接给窗口投 WM_KEYDOWN/UP（兜底，未必有效）。
  脚本会把实际用的方式和是否抢到前台打印出来 —— 日志里没出现预期消息时，先看这行。

.PARAMETER Key
  按键名：F1–F12，或 A–Z / 0–9 / ESC / SPACE / ENTER。

.PARAMETER ProcessName
  默认 EscapeFromTarkov。

.PARAMETER WindowClass
  要操作的顶层窗口类名，默认 UnityWndClass。

.EXAMPLE
  .\send-key.ps1 F7
#>
param(
    [Parameter(Mandatory = $true)][string]$Key,
    [string]$ProcessName = 'EscapeFromTarkov',
    [string]$WindowClass = 'UnityWndClass',
    [int]$HoldMs = 60,
    [int]$FocusWaitMs = 1200
)

$ErrorActionPreference = 'Stop'

# ---- 按键名 -> 虚拟键码 ----
$vk = 0
if ($Key -match '^F([1-9]|1[0-2])$') { $vk = 0x6F + [int]$Matches[1] }
elseif ($Key -match '^[A-Za-z]$') { $vk = [int][char]([string]$Key).ToUpper() }
elseif ($Key -match '^[0-9]$') { $vk = 0x30 + [int]$Key }
else {
    $named = @{ 'ESC' = 0x1B; 'SPACE' = 0x20; 'ENTER' = 0x0D; 'TAB' = 0x09 }
    $upper = ([string]$Key).ToUpper()
    if ($named.ContainsKey($upper)) { $vk = $named[$upper] } else { throw "不认识的按键名：$Key" }
}

Add-Type -Namespace SeTools -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool SetActiveWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool SetFocus(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
[DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
[DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
[DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
[DllImport("user32.dll")] public static extern uint MapVirtualKey(uint uCode, uint uMapType);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
[DllImport("user32.dll")] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
'@

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { throw "找不到进程 $ProcessName" }

# ---- 找 Unity 主窗口（不是 BepInEx 控制台） ----
$script:hits = @()
$cb = [SeTools.Native+EnumProc]{
    param($h, $l)
    $owner = [uint32]0
    [void][SeTools.Native]::GetWindowThreadProcessId($h, [ref]$owner)
    if ($owner -eq $proc.Id) {
        $cls = New-Object System.Text.StringBuilder 200
        [void][SeTools.Native]::GetClassName($h, $cls, 200)
        $script:hits += [pscustomobject]@{ Hwnd = $h; Class = $cls.ToString(); Visible = [SeTools.Native]::IsWindowVisible($h) }
    }
    return $true
}
[void][SeTools.Native]::EnumWindows($cb, [IntPtr]::Zero)

$target = $script:hits | Where-Object { $_.Class -eq $WindowClass -and $_.Visible } | Select-Object -First 1
if (-not $target) {
    $target = $script:hits | Where-Object { $_.Class -eq $WindowClass } | Select-Object -First 1
}
if (-not $target) {
    $all = ($script:hits | ForEach-Object { "0x$('{0:X}' -f [int64]$_.Hwnd)/$($_.Class)" }) -join ', '
    throw "进程 $($proc.Id) 里找不到类名 $WindowClass 的窗口（现有：$all）"
}
$hwnd = [IntPtr]$target.Hwnd
Write-Host "target: $ProcessName pid=$($proc.Id) hwnd=0x$('{0:X}' -f [int64]$hwnd) class=$($target.Class)"

$KEYUP = 0x0002
$EXTENDED = 0x0001

# ---- 2. 抢前台 + 抢键盘焦点 ----
[SeTools.Native]::keybd_event(0x12, 0, $EXTENDED, [UIntPtr]::Zero)              # ALT down（解锁前台切换限制）
[SeTools.Native]::keybd_event(0x12, 0, $EXTENDED -bor $KEYUP, [UIntPtr]::Zero)  # ALT up

$curThread = [SeTools.Native]::GetCurrentThreadId()
$targetPid = [uint32]0
$targetThread = [SeTools.Native]::GetWindowThreadProcessId($hwnd, [ref]$targetPid)
[void][SeTools.Native]::AttachThreadInput($curThread, $targetThread, $true)
[void][SeTools.Native]::ShowWindow($hwnd, 5)   # SW_SHOW
[void][SeTools.Native]::BringWindowToTop($hwnd)
[void][SeTools.Native]::SetActiveWindow($hwnd)
$ok = [SeTools.Native]::SetForegroundWindow($hwnd)
[void][SeTools.Native]::SetFocus($hwnd)
[void][SeTools.Native]::AttachThreadInput($curThread, $targetThread, $false)

$gotFocus = $false
$deadline = [DateTime]::UtcNow.AddMilliseconds($FocusWaitMs)
do {
    Start-Sleep -Milliseconds 50
    if ([SeTools.Native]::GetForegroundWindow() -eq $hwnd) { $gotFocus = $true; break }
} while ([DateTime]::UtcNow -lt $deadline)

# ---- 3. 发按键 ----
if ($gotFocus) {
    Start-Sleep -Milliseconds 250   # 让 Unity 消化一次焦点变化（OnApplicationFocus），避免这一帧的按键被丢掉
    $scan = [SeTools.Native]::MapVirtualKey([uint32]$vk, 0)
    [SeTools.Native]::keybd_event([byte]$vk, [byte]$scan, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds $HoldMs
    [SeTools.Native]::keybd_event([byte]$vk, [byte]$scan, $KEYUP, [UIntPtr]::Zero)
    Write-Host "sent $Key (vk=0x$('{0:X2}' -f $vk)) via keybd_event; foreground=yes (SetForegroundWindow=$ok)"
}
else {
    $scan = [SeTools.Native]::MapVirtualKey([uint32]$vk, 0)
    $down = [int64](1 -bor ([int64]$scan -shl 16))
    $up = [int64](1 -bor ([int64]$scan -shl 16) -bor 0xC0000000)
    [void][SeTools.Native]::PostMessage($hwnd, 0x0100, [IntPtr]$vk, [IntPtr]$down)
    Start-Sleep -Milliseconds $HoldMs
    [void][SeTools.Native]::PostMessage($hwnd, 0x0101, [IntPtr]$vk, [IntPtr]$up)
    Write-Host "sent $Key (vk=0x$('{0:X2}' -f $vk)) via PostMessage; foreground=no (SetForegroundWindow=$ok)"
    Write-Warning '没抢到前台：用的是消息兜底，若日志里没有预期输出，就手动点一下游戏窗口再重跑'
}
