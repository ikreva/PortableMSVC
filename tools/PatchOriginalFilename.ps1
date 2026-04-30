param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $Path
)

$old = [Text.Encoding]::Unicode.GetBytes("PortableMSVC.dll")
$new = [Text.Encoding]::Unicode.GetBytes("PortableMSVC.exe")

foreach ($file in $Path) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        continue
    }

    $bytes = [IO.File]::ReadAllBytes($file)
    $patched = $false

    for ($i = 0; $i -le $bytes.Length - $old.Length; $i++) {
        $match = $true
        for ($j = 0; $j -lt $old.Length; $j++) {
            if ($bytes[$i + $j] -ne $old[$j]) {
                $match = $false
                break
            }
        }

        if ($match) {
            [Array]::Copy($new, 0, $bytes, $i, $new.Length)
            $patched = $true
            $i += $old.Length - 1
        }
    }

    if ($patched) {
        [IO.File]::WriteAllBytes($file, $bytes)
    }
}
