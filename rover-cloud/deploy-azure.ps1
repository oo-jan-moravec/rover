#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Deploy RoverCloudRelay to Azure App Service

.DESCRIPTION
    This script creates and deploys the RoverCloudRelay to Azure App Service.
    It creates all necessary Azure resources including:
    - Resource Group
    - App Service Plan (Linux, B1 tier - cheapest option with WebSockets)
    - App Service with .NET 10
    - Application Insights (optional)

.PARAMETER ResourceGroup
    Name of the Azure Resource Group (default: rg-rover-relay)

.PARAMETER Location
    Azure region (default: eastus)

.PARAMETER AppName
    Name of the App Service (must be globally unique)

.PARAMETER RoverApiKey
    API key for rover authentication

.PARAMETER ClientAccessKey
    Access key for client authentication

.PARAMETER SkipDeploy
    Only create resources, don't deploy code

.EXAMPLE
    ./deploy-azure.ps1 -AppName "my-rover-relay" -RoverApiKey "secret123" -ClientAccessKey "clientkey456"
#>

param(
    [string]$ResourceGroup = "rg-rover-relay",
    [string]$Location = "germanywestcentral",
    [Parameter(Mandatory=$true)]
    [string]$AppName,
    [Parameter(Mandatory=$true)]
    [string]$RoverApiKey,
    [Parameter(Mandatory=$true)]
    [string]$ClientAccessKey,
    [switch]$SkipDeploy,
    [switch]$SkipResourceCreation
)

$ErrorActionPreference = "Stop"

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  Rover Cloud Relay - Azure Deployment" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""

# Check if Azure CLI is installed
try {
    $null = az --version
} catch {
    Write-Host "ERROR: Azure CLI is not installed. Install from https://aka.ms/installazurecli" -ForegroundColor Red
    exit 1
}

# Check if logged in
$account = az account show 2>$null | ConvertFrom-Json
if (-not $account) {
    Write-Host "Not logged in to Azure. Running 'az login'..." -ForegroundColor Yellow
    az login
    $account = az account show | ConvertFrom-Json
}

Write-Host "Using Azure subscription: $($account.name)" -ForegroundColor Green
Write-Host "  ID: $($account.id)" -ForegroundColor Gray
Write-Host ""

$AppServicePlan = "${AppName}-plan"

if (-not $SkipResourceCreation) {
    # Check if Resource Group exists
    Write-Host "Checking Resource Group: $ResourceGroup..." -ForegroundColor Yellow
    $existingRg = az group show --name $ResourceGroup 2>$null | ConvertFrom-Json
    
    if ($existingRg) {
        $existingLocation = $existingRg.location
        Write-Host "  Resource Group exists in '$existingLocation'" -ForegroundColor Green
        # Use the existing location for all resources
        $Location = $existingLocation
    } else {
        Write-Host "Creating Resource Group: $ResourceGroup in $Location..." -ForegroundColor Yellow
        az group create --name $ResourceGroup --location $Location --output none
        Write-Host "  Resource Group created." -ForegroundColor Green
    }

    # Check if App Service Plan exists
    Write-Host "Checking App Service Plan: $AppServicePlan..." -ForegroundColor Yellow
    $existingPlan = az appservice plan show --name $AppServicePlan --resource-group $ResourceGroup 2>$null | ConvertFrom-Json
    
    if ($existingPlan) {
        Write-Host "  App Service Plan already exists." -ForegroundColor Green
    } else {
        Write-Host "Creating App Service Plan: $AppServicePlan (Linux B1)..." -ForegroundColor Yellow
        az appservice plan create `
            --name $AppServicePlan `
            --resource-group $ResourceGroup `
            --location $Location `
            --sku B1 `
            --is-linux `
            --output none
        Write-Host "  App Service Plan created." -ForegroundColor Green
    }

    # Check if Web App exists
    Write-Host "Checking Web App: $AppName..." -ForegroundColor Yellow
    $existingApp = az webapp show --name $AppName --resource-group $ResourceGroup 2>$null | ConvertFrom-Json
    
    if ($existingApp) {
        Write-Host "  Web App already exists." -ForegroundColor Green
    } else {
        Write-Host "Creating Web App: $AppName..." -ForegroundColor Yellow
        az webapp create `
            --name $AppName `
            --resource-group $ResourceGroup `
            --plan $AppServicePlan `
            --runtime "DOTNETCORE:10.0" `
            --output none
        
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  ERROR: Failed to create Web App." -ForegroundColor Red
            exit 1
        }
        Write-Host "  Web App created." -ForegroundColor Green
    }

    # Enable WebSockets (idempotent)
    Write-Host "Enabling WebSockets..." -ForegroundColor Yellow
    az webapp config set `
        --name $AppName `
        --resource-group $ResourceGroup `
        --web-sockets-enabled true `
        --output none
    Write-Host "  WebSockets enabled." -ForegroundColor Green

    # Set HTTPS only (idempotent)
    Write-Host "Configuring HTTPS only..." -ForegroundColor Yellow
    az webapp update `
        --name $AppName `
        --resource-group $ResourceGroup `
        --https-only true `
        --output none
    Write-Host "  HTTPS only enabled." -ForegroundColor Green

    # Azure Communication Services - use ARM API directly (CLI extension is slow/buggy)
    $AcsName = "${AppName}-acs"
    $acsConnStr = "MANUAL_CONFIG_REQUIRED"
    
    Write-Host "Creating/updating Azure Communication Services: $AcsName..." -ForegroundColor Yellow
    
    # Use az resource instead of az communication (more reliable)
    $acsExists = az resource show `
        --resource-group $ResourceGroup `
        --name $AcsName `
        --resource-type "Microsoft.Communication/communicationServices" `
        2>$null | ConvertFrom-Json
    
    if ($acsExists) {
        Write-Host "  Azure Communication Services already exists." -ForegroundColor Green
    } else {
        # Create via ARM
        az resource create `
            --resource-group $ResourceGroup `
            --name $AcsName `
            --resource-type "Microsoft.Communication/communicationServices" `
            --properties '{"dataLocation": "Europe"}' `
            --location "global" `
            --output none 2>$null
        
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  Azure Communication Services created." -ForegroundColor Green
        } else {
            Write-Host "  Warning: Could not create ACS. Create manually in Azure Portal." -ForegroundColor Yellow
        }
    }

    # Get connection string via ARM
    Write-Host "Getting ACS connection string..." -ForegroundColor Yellow
    $acsKeys = az resource invoke-action `
        --resource-group $ResourceGroup `
        --name $AcsName `
        --resource-type "Microsoft.Communication/communicationServices" `
        --action listKeys `
        2>$null | ConvertFrom-Json
    
    if ($acsKeys -and $acsKeys.primaryConnectionString) {
        $acsConnStr = $acsKeys.primaryConnectionString
        Write-Host "  Got ACS connection string." -ForegroundColor Green
    } else {
        Write-Host "  Warning: Could not get ACS connection string." -ForegroundColor Yellow
        Write-Host "  Set RoverRelay__AzureCommunicationServices__ConnectionString manually in App Service." -ForegroundColor Yellow
    }

    # Configure application settings (idempotent - overwrites existing)
    Write-Host "Configuring application settings..." -ForegroundColor Yellow
    
    $settings = @(
        "RoverRelay__RoverApiKey=$RoverApiKey",
        "RoverRelay__ClientAccessKey=$ClientAccessKey",
        "RoverRelay__AzureCommunicationServices__ConnectionString=$acsConnStr"
    )
    
    az webapp config appsettings set `
        --name $AppName `
        --resource-group $ResourceGroup `
        --settings @settings `
        --output none
    Write-Host "  Application settings configured." -ForegroundColor Green
}

if (-not $SkipDeploy) {
    # Build and deploy
    Write-Host ""
    Write-Host "Building application..." -ForegroundColor Yellow
    
    $projectPath = Join-Path $PSScriptRoot "RoverCloudRelay"
    $publishPath = Join-Path $projectPath "bin/publish"
    
    Push-Location $projectPath
    try {
        dotnet publish -c Release -o $publishPath
        if ($LASTEXITCODE -ne 0) {
            throw "Build failed"
        }
        Write-Host "  Build successful." -ForegroundColor Green
        
        # Create zip for deployment
        Write-Host "Creating deployment package..." -ForegroundColor Yellow
        $zipPath = Join-Path $PSScriptRoot "deploy.zip"
        if (Test-Path $zipPath) {
            Remove-Item $zipPath -Force
        }
        
        Compress-Archive -Path "$publishPath/*" -DestinationPath $zipPath -Force
        Write-Host "  Package created: $zipPath" -ForegroundColor Green
        
        # Deploy to Azure
        Write-Host "Deploying to Azure App Service..." -ForegroundColor Yellow
        az webapp deploy `
            --name $AppName `
            --resource-group $ResourceGroup `
            --src-path $zipPath `
            --type zip `
            --output none
        Write-Host "  Deployment successful!" -ForegroundColor Green
        
        # Clean up
        Remove-Item $zipPath -Force
        Remove-Item $publishPath -Recurse -Force
        
    } finally {
        Pop-Location
    }
}

Write-Host ""
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  Deployment Complete!" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""

$appUrl = "https://${AppName}.azurewebsites.net"

Write-Host "Your Rover Cloud Relay is available at:" -ForegroundColor Green
Write-Host "  $appUrl" -ForegroundColor White
Write-Host ""
Write-Host "Endpoints:" -ForegroundColor Yellow
Write-Host "  Health:  $appUrl/health"
Write-Host "  Status:  $appUrl/status"
Write-Host "  Rover:   $appUrl/rover (SignalR hub)"
Write-Host "  Client:  $appUrl/client (SignalR hub)"
Write-Host ""
Write-Host "Configuration for Rover (appsettings.json):" -ForegroundColor Yellow
Write-Host @"
{
  "CloudRelay": {
    "Enabled": true,
    "Url": "$appUrl/rover",
    "ApiKey": "$RoverApiKey"
  }
}
"@ -ForegroundColor Gray
Write-Host ""
Write-Host "Client URL (add to bookmark):" -ForegroundColor Yellow

# URL encode the values for the client URL
$encodedRelay = [System.Web.HttpUtility]::UrlEncode($appUrl)
$encodedKey = [System.Web.HttpUtility]::UrlEncode($ClientAccessKey)
Write-Host "  http://ROVER_LOCAL_IP:8080/?cloud=1&relay=$encodedRelay&key=$encodedKey" -ForegroundColor Gray
Write-Host ""
Write-Host "Or host the static files on the cloud relay itself for fully remote access." -ForegroundColor Gray
Write-Host ""

# Cost estimate
Write-Host "Estimated Monthly Cost (B1 tier):" -ForegroundColor Yellow
Write-Host "  ~\$13-15 USD/month (East US pricing)" -ForegroundColor Gray
Write-Host "  Tip: Scale down to F1 (free) for testing, but WebSockets won't work." -ForegroundColor Gray
Write-Host ""
