# Click a button by name in the LVA app via UI Automation
param([string]$ButtonName = "Питомец (окно)")

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$proc = Get-Process LVA.App -ErrorAction Stop
$root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)

$condition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, $ButtonName)
$button = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)

if ($button) {
    $pattern = $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
    Write-Output "clicked '$ButtonName'"
} else {
    Write-Output "button '$ButtonName' not found"
}
