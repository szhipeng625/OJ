param([string]$Stage="all", [string]$User="uivirt", [string]$Pass="123456")
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
. D:\OJ\scripts\ui_helper.ps1

$global:proc = Get-Process client -ErrorAction SilentlyContinue | Select-Object -First 1
$global:w = $null

function Refresh-Window {
  Start-Sleep -Milliseconds 800
  $global:proc.Refresh()
  $deadline=(Get-Date).AddSeconds(15)
  while((Get-Date) -lt $deadline){
    $global:proc.Refresh()
    if($global:proc.MainWindowHandle -ne 0){
      $global:w=[System.Windows.Automation.AutomationElement]::FromHandle($global:proc.MainWindowHandle)
      if($global:w){ break }
    }
    Start-Sleep -Milliseconds 400
  }
  Write-Host "WINDOW: $($global:w.Current.Name)"
}

function Do-Login {
  Refresh-Window
  $edits=Find-All $global:w "" "Edit"
  Set-Text $edits[0] $User
  $edits[1].SetFocus()
  Start-Sleep -Milliseconds 400
  [System.Windows.Forms.SendKeys]::SendWait("^a")
  Start-Sleep -Milliseconds 150
  foreach($ch in $Pass.ToCharArray()){ [System.Windows.Forms.SendKeys]::SendWait("$ch"); Start-Sleep -Milliseconds 80 }
  Start-Sleep -Milliseconds 300
  Click (Find-One $global:w "登录" "Button")
  Start-Sleep -Seconds 3
  Refresh-Window
}

function Open-ContestTab {
  $tab=Find-One $global:w "比赛" "TabItem"
  if(-not $tab){ throw "没有比赛 Tab" }
  Select-Item $tab
  Start-Sleep -Milliseconds 800
}

function Select-Contest {
  $items=Find-All $global:w "" "ListItem"
  Write-Host "ListItem 数量: $($items.Count)"
  foreach($i in $items){ Write-Host "  item: $($i.Current.Name)" }
  Select-Item $items[0]
  Start-Sleep -Seconds 2
}

function Do-Register {
  $btn=Find-One $global:w "报名" "Button"
  Write-Host "报名按钮 enabled=$($btn.Current.IsEnabled)"
  Click $btn
  Start-Sleep -Seconds 2
  $enter=Find-One $global:w "进入比赛" "Button"
  Write-Host "进入比赛按钮 offscreen=$($enter.Current.IsOffscreen) enabled=$($enter.Current.IsEnabled)"
}

function Do-Enter {
  Click (Find-One $global:w "进入比赛" "Button")
  Start-Sleep -Seconds 2
  $items=Find-All $global:w "" "ListItem"
  Write-Host "进入后题目数: $($items.Count)"
  foreach($i in $items){ Write-Host "  $($i.Current.Name)" }
}

function Do-Submit {
  param([string]$CodeFile)
  $items=Find-All $global:w "" "ListItem"
  Select-Item $items[0]
  Start-Sleep -Seconds 2
  # AvalonEdit：聚焦后剪贴板粘贴
  $code=Get-Content $CodeFile -Raw -Encoding UTF8
  Set-Clipboard -Value $code
  # 代码编辑器是自定义控件，用 Tab 导航定位较脆弱；按 AutomationId/类名找 Document
  $docs=Find-All $global:w "" "Document"
  Write-Host "Document 控件数: $($docs.Count)"
  if($docs.Count -gt 0){
    $docs[$docs.Count-1].SetFocus()
  }
  Start-Sleep -Milliseconds 500
  [System.Windows.Forms.SendKeys]::SendWait("^a")
  Start-Sleep -Milliseconds 200
  [System.Windows.Forms.SendKeys]::SendWait("^v")
  Start-Sleep -Milliseconds 800
  Click (Find-One $global:w "提交判题" "Button")
  Write-Host "已提交，等待判题..."
  $deadline=(Get-Date).AddSeconds(60)
  $last=""
  while((Get-Date) -lt $deadline){
    Start-Sleep -Seconds 2
    $txt=Find-All $global:w "" "Text"
    $line=($txt | ForEach-Object { $_.Current.Name } | Where-Object { $_ -like "结果：*" }) -join "|"
    if($line -ne $last){ Write-Host "  $line"; $last=$line }
    if($line -match "AC|WA|TLE|RE|CE|SE|无响应|请求失败"){ break }
  }
}

function Dump-Texts([string]$tag) {
  Write-Host "----- TEXTS [$tag] -----"
  foreach($t in (Find-All $global:w "" "Text")){
    $n=$t.Current.Name
    if($n){ Write-Host "  T: $n" }
  }
}

Refresh-Window
switch($Stage){
  "login"   { Do-Login }
  "list"    { Open-ContestTab; Select-Contest; Dump-Texts "list"; Shot "D:\OJ\xshd_dump\c1_list.png" }
  "register"{ Do-Register; Dump-Texts "registered"; Shot "D:\OJ\xshd_dump\c2_registered.png" }
  "enter"   { Do-Enter; Dump-Texts "room"; Shot "D:\OJ\xshd_dump\c3_room.png" }
  "submit"  { Do-Submit "D:\OJ\server\problems\3\std.cpp"; Dump-Texts "aftersubmit"; Shot "D:\OJ\xshd_dump\c4_submit.png" }
  "board"   { Click (Find-One $global:w "排行榜" "Button"); Start-Sleep -Seconds 2; Dump-Texts "board"; Shot "D:\OJ\xshd_dump\c5_board.png" }
  "subs"    { Click (Find-One $global:w "提交记录" "Button"); Start-Sleep -Seconds 2; Dump-Texts "subs"; Shot "D:\OJ\xshd_dump\c6_subs.png" }
  "back"    {
    Click (Find-One $global:w "做题" "Button"); Start-Sleep -Seconds 1
    Click (Find-One $global:w "返回比赛列表" "Button"); Start-Sleep -Seconds 2
    Dump-Texts "back"; Shot "D:\OJ\xshd_dump\c7_back.png"
  }
  default {
    Do-Login
    Open-ContestTab
    Select-Contest
    Shot "D:\OJ\xshd_dump\c1_list.png"
    Do-Register
    Shot "D:\OJ\xshd_dump\c2_registered.png"
    Do-Enter
    Shot "D:\OJ\xshd_dump\c3_room.png"
    Do-Submit "D:\OJ\server\problems\3\std.cpp"
    Shot "D:\OJ\xshd_dump\c4_submit.png"
    Click (Find-One $global:w "排行榜" "Button"); Start-Sleep -Seconds 2
    Dump-Texts "board"; Shot "D:\OJ\xshd_dump\c5_board.png"
    Click (Find-One $global:w "提交记录" "Button"); Start-Sleep -Seconds 2
    Dump-Texts "subs"; Shot "D:\OJ\xshd_dump\c6_subs.png"
    Click (Find-One $global:w "做题" "Button"); Start-Sleep -Seconds 1
    Click (Find-One $global:w "返回比赛列表" "Button"); Start-Sleep -Seconds 2
    Shot "D:\OJ\xshd_dump\c7_back.png"
  }
}
