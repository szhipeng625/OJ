# UI 自动化辅助函数（UIAutomation managed API）
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

function Get-MainWindow($proc) {
  $deadline=(Get-Date).AddSeconds(20)
  while((Get-Date) -lt $deadline){
    $proc.Refresh()
    if($proc.MainWindowHandle -ne 0){
      $el=[System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
      if($el){ return $el }
    }
    Start-Sleep -Milliseconds 400
  }
  throw "main window timeout"
}

function Find-All($root, [string]$Name, [string]$Cls) {
  $cond=New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::$Cls)
  $all=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,$cond)
  if($Name){
    $hit=@()
    foreach($e in $all){ if($e.Current.Name -like "*$Name*"){ $hit+=$e } }
    return ,$hit
  }
  return ,$all
}

function Find-One($root, [string]$Name, [string]$Cls) {
  $r=Find-All $root $Name $Cls
  if($r.Count -ge 1){ return $r[0] }
  return $null
}

function Click($el) {
  if($null -eq $el){ throw "click target null" }
  $pat=$el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
  $pat.Invoke()
}

function Select-Item($el) {
  $pat=$el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
  $pat.Select()
}

function Set-Text($el,[string]$text) {
  $pat=$el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
  $pat.SetValue($text)
}

function Shot($path) {
  Add-Type -AssemblyName System.Windows.Forms
  $b=[System.Windows.Forms.SystemInformation]::VirtualScreen
  $bmp=New-Object System.Drawing.Bitmap $b.Width,$b.Height
  $g=[System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($b.X,$b.Y,0,0,$bmp.Size)
  $bmp.Save($path,[System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose();$bmp.Dispose()
  Write-Host "shot -> $path"
}
