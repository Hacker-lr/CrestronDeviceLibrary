$ErrorActionPreference = 'SilentlyContinue'
$targets = @('192.168.0.24','192.168.0.25')
foreach ($ip in $targets) {
  $c = New-Object System.Net.Sockets.TcpClient
  try {
    $c.ReceiveTimeout = 2500
    $c.Connect($ip, 23)
    Write-Output "[$ip:23] CONNECTED"
    $s = $c.GetStream()
    $s.ReadTimeout = 2500
    $buf = New-Object byte[] 2048
    Start-Sleep -Milliseconds 700
    $n = $s.Read($buf, 0, $buf.Length)
    $txt = [System.Text.Encoding]::UTF8.GetString($buf, 0, [Math]::Max(0,$n))
    $esc = $txt.Replace("`r",'<CR>').Replace("`n",'<LF>')
    Write-Output ("  raw({0}B): {1}" -f $n, $esc)
  } catch {
    Write-Output "[$ip:23] ERR: $($_.Exception.Message)"
  }
  $c.Dispose()
}