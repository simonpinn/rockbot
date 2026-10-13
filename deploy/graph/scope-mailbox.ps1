# Grants the RockBot app "Application Mail.Read" on exactly one mailbox via Exchange
# RBAC for Applications, or removes that grant. Idempotent. Run by 02-scope-mailbox.sh
# and teardown.sh inside the PowerShell container; inputs come from the environment.
$ErrorActionPreference = 'Stop'

$action     = if ($env:ACTION) { $env:ACTION } else { 'apply' }
$appId      = $env:APP_ID
$spObjectId = $env:SP_OBJECT_ID
$mailbox    = $env:MAILBOX
$appName    = $env:APP_NAME
if (-not $appId) { throw 'APP_ID is required' }

# Names are derived from the app id so reruns find what earlier runs created.
$scopeName      = "RockBot-Mailbox-$appId"
$assignmentName = "RockBot-MailRead-$appId"
$role           = 'Application Mail.Read'

function Find([scriptblock] $lookup) { try { & $lookup } catch { $null } }

if (-not (Get-Module -ListAvailable ExchangeOnlineManagement)) {
    Write-Host '==> Installing ExchangeOnlineManagement module (cached for later runs)'
    Install-Module ExchangeOnlineManagement -Scope CurrentUser -Force -AllowClobber
}
Import-Module ExchangeOnlineManagement

Write-Host '==> Connecting to Exchange Online: follow the device-code prompt below'
Connect-ExchangeOnline -Device -ShowBanner:$false

try {
    $sp         = Get-ServicePrincipal | Where-Object AppId -eq $appId
    $scope      = Find { Get-ManagementScope -Identity $scopeName }
    $assignment = Find { Get-ManagementRoleAssignment -Identity $assignmentName }

    if ($action -eq 'remove') {
        if ($assignment) { Remove-ManagementRoleAssignment -Identity $assignmentName -Confirm:$false; Write-Host "==> Removed role assignment $assignmentName" }
        if ($scope)      { Remove-ManagementScope -Identity $scopeName -Confirm:$false; Write-Host "==> Removed management scope $scopeName" }
        if ($sp)         { Remove-ServicePrincipal -Identity $sp.Identity -Confirm:$false; Write-Host "==> Removed Exchange service principal for $appId" }
        return
    }

    if (-not $mailbox -or -not $spObjectId) { throw 'MAILBOX and SP_OBJECT_ID are required' }

    if (-not $sp) {
        $sp = New-ServicePrincipal -AppId $appId -ObjectId $spObjectId -DisplayName $appName
        Write-Host "==> Created Exchange service principal for $appId"
    } else {
        Write-Host "==> Found Exchange service principal for $appId"
    }

    # Match on the primary SMTP address Exchange actually has, even if MAILBOX was given as a UPN or alias.
    $smtp   = (Get-Recipient -Identity $mailbox).PrimarySmtpAddress.ToString()
    $filter = "PrimarySmtpAddress -eq '$smtp'"
    if (-not $scope) {
        New-ManagementScope -Name $scopeName -RecipientRestrictionFilter $filter | Out-Null
        Write-Host "==> Created management scope $scopeName ($filter)"
    } elseif ("$($scope.RecipientFilter)" -ne $filter) {
        Set-ManagementScope -Identity $scopeName -RecipientRestrictionFilter $filter
        Write-Host "==> Updated management scope $scopeName to ($filter)"
    } else {
        Write-Host "==> Found management scope $scopeName ($filter)"
    }

    if (-not $assignment) {
        New-ManagementRoleAssignment -Name $assignmentName -App $sp.Identity -Role $role -CustomResourceScope $scopeName | Out-Null
        Write-Host "==> Created role assignment $assignmentName ($role, scoped to $smtp)"
    } elseif ("$($assignment.CustomResourceScope)" -ne $scopeName) {
        Set-ManagementRoleAssignment -Identity $assignmentName -CustomResourceScope $scopeName
        Write-Host "==> Re-pointed role assignment $assignmentName at $scopeName"
    } else {
        Write-Host "==> Found role assignment $assignmentName"
    }

    Write-Host "==> Effective permissions for $smtp (InScope should be True for $role):"
    Test-ServicePrincipalAuthorization -Identity $sp.Identity -Resource $smtp | Format-List RoleName, GrantedPermissions, AllowedResourceScope, InScope
}
finally {
    Disconnect-ExchangeOnline -Confirm:$false | Out-Null
}
