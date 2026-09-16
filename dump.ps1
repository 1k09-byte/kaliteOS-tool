 = 'System\CurrentControlSet\Enum\PCI\VEN_10DE&DEV_2783&SUBSYS_41391458&REV_A1\4&D0BDF66&0&0009\Device Parameters\Interrupt Management\Affinity Policy'
 = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey()
 = .GetValue('AssignmentSetOverride')
if () {
    Write-Host ""Raw String: ""
} else {
    Write-Host ""No bytes found""
}
