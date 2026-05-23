$txt = [System.IO.File]::ReadAllText("MainWindow.xaml.cs")
$openCount = ($txt.ToCharArray() | Where-Object { $_ -eq '{' }).Count
$closeCount = ($txt.ToCharArray() | Where-Object { $_ -eq '}' }).Count
Write-Host "Open: $openCount, Close: $closeCount"
