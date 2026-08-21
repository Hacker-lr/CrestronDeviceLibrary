$ErrorActionPreference = 'SilentlyContinue'
$t = @('61794','23','41795','41794','22')
foreach ($port in $t) {
  $c = New-Object System.Net.Sockets.TcpClient
  try {
    $c.ReceiveTimeout = 2500
    $c.Connect('192.168.0.100', [int]$port)
    Write-Output "[port $port] CONNECTED"
    $s = $c.GetStream()
    $s.ReadTimeout = 2500
    $buf = New-Object byte[] 2048
    Start-Sleep -Milliseconds 700
    $n = $s.Read($buf, 0, $buf.Length)
    $txt = [System.Text.Encoding]::ASCII.GetString($buf, 0, [Math]::Max(0,$n))
    $esc = $txt.Replace("`r",'<CR>').Replace("`n",'<LF>')
    Write-Output ("  raw({0}B): {1}" -f $n, $esc)
  } catch {
    Write-Output "[port $port] ERR: $($_.Exception.Message)"
  }
  $c.Dispose()
}