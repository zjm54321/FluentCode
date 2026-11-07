param([int]$ProcessId, [string]$LogPath)
$ErrorActionPreference = 'Stop'
try {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $processCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $processCondition)
    if (-not $window) { throw 'Prototype window not found by process ID.' }
    $editorCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'ChipEditor')
    $editor = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $editorCondition)
    if (-not $editor) { throw 'ChipEditor not found.' }
    $pattern = $null
    if (-not $editor.TryGetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern, [ref]$pattern)) { throw 'No external UIA TextPattern.' }
    $range = $pattern.DocumentRange
    $text = $range.GetText(-1)
    $lines = @("Editor name=$($editor.Current.Name)", "TextPattern text=$($text.Replace("`r", '\r').Replace("`n", '\n'))")
    $children = $range.GetChildren()
    $lines += "Embedded children=$($children.Count)"
    $altExposed = $text.Contains('same label')
    foreach ($child in $children) {
        $lines += "Child type=$($child.Current.ControlType.ProgrammaticName) name=$($child.Current.Name)"
        if ($child.Current.Name.Contains('same label')) { $altExposed = $true }
    }
    $lines += "Alt exposed=$altExposed"
    $lines | Set-Content -Path $LogPath -Encoding UTF8
    if (-not $altExposed) { exit 1 }
    exit 0
} catch {
    $_.ToString() | Set-Content -Path $LogPath -Encoding UTF8
    exit 2
}
