# Copyright (c) Microsoft. All rights reserved.
# Licensed under the MIT license. See LICENSE file in the project root for full license information.

# This script is used to change screen resolution on remote machine
# Return Value: 0 indicates task is started successfully; -1 indicates failed to run the specified task

# Run Task to change remote screen orientation

$cmdOutput = ""
$securePassword = New-Object SecureString
foreach ($char in $ptfprop_SUTUserPassword.ToCharArray()) {
    $securePassword.AppendChar($char)
}
$Credential = New-Object System.Management.Automation.PSCredential($ptfprop_SUTUserName,$securePassword)

$sessionM = $null;

try
{
    Get-PSSession|Remove-PSSession
	$sessionM = New-PSSession -ComputerName $ptfprop_SUTName -Credential $Credential
}
catch
{
}

$path = "/RDP-TestSuite-ClientEP/Scripts"
$scriptblock = {
	param([int]$width, [int]$height, [string]$path)
    $taskname = "ChangeResolution" + $width + "X" + $height
    $systemDrive = $env:SystemDrive
	$script = $systemDrive + $path + "/ChangeResolution.ps1"
	cmd /c schtasks /Create /SC Weekly /TN $taskname /TR "powershell $script $width $height" /IT /F
	cmd /c schtasks /Run /TN $taskname
	cmd /c schtasks /Delete /TN $taskname /F
	}
	
if ($null -eq $sessionM)
{
    $cmdOutput = Invoke-Command -HostName $ptfprop_SUTName -UserName $ptfprop_SUTUserName -ScriptBlock $scriptblock -ArgumentList ($width, $height, $path)
}
else
{
    $cmdOutput = Invoke-Command -Session $sessionM -ScriptBlock $scriptblock -ArgumentList ($width, $height, $path)
}

$cmdOutput | out-file "./ChangeResolution.log"

if($cmdOutput -ne $null)
{
    if($cmdOutput.GetType().IsArray)
    {
        $cmdOutput = $cmdOutput[0]
    }
    if($cmdOutput.ToUpper().Contains("ERROR"))
    {
        return -1 # operation failed
    }
}

return 0  #operation succeed
