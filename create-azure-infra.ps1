# Create TimeClock Azure infrastructure
$SUB = "9db732dc-bdca-49b0-a0cb-3d20a89a3572"
$RG = "rg-timeclock"
$LOCATION = "centralus"
$PLAN = "plan-timeclock"
$APP = "newheights-timeclock"

Write-Host "Step 1: Create resource group..."
az group create --name $RG --location $LOCATION --subscription $SUB

Write-Host "Step 2: Create App Service Plan (B2 Windows for .NET 8)..."
az appservice plan create --name $PLAN --resource-group $RG --location $LOCATION --sku B2 --subscription $SUB

Write-Host "Step 3: Create Web App..."
az webapp create --name $APP --resource-group $RG --plan $PLAN --runtime "DOTNET:8.0" --subscription $SUB

Write-Host "Step 4: Enable HTTPS only..."
az webapp update --name $APP --resource-group $RG --https-only true --subscription $SUB

Write-Host "Step 5: Set connection string..."
az webapp config connection-string set --name $APP --resource-group $RG --settings DefaultConnection="Server=newheights-idcard-sql.database.windows.net;Database=IDCardPrinterDB;User Id=sqladmin;Password=Gaya0066;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;Command Timeout=30;" --connection-string-type SQLAzure --subscription $SUB

Write-Host "Done. App URL: https://$APP.azurewebsites.net"
