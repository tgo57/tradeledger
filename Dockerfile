FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution and project files
COPY ["src/TradeLedger.Web/TradeLedger.Web.csproj", "src/TradeLedger.Web/"]
COPY ["src/TradeLedger.Core/TradeLedger.Core.csproj", "src/TradeLedger.Core/"]
COPY ["src/TradeLedger.Data/TradeLedger.Data.csproj", "src/TradeLedger.Data/"]
COPY ["src/TradeLedger.Importers/TradeLedger.Importers.csproj", "src/TradeLedger.Importers/"]

# Restore dependencies
RUN dotnet restore "src/TradeLedger.Web/TradeLedger.Web.csproj"

# Copy everything and build
COPY . .
RUN dotnet publish "src/TradeLedger.Web/TradeLedger.Web.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Copy published output
COPY --from=build /app/publish .

# Railway uses PORT environment variable
ENV ASPNETCORE_URLS=http://+:$PORT
EXPOSE $PORT

ENTRYPOINT ["dotnet", "TradeLedger.Web.dll"]