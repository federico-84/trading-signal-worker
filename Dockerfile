FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["PortfolioSignalWorker.csproj", "."]
RUN dotnet restore "PortfolioSignalWorker.csproj"
COPY . .
WORKDIR "/src"
RUN dotnet build "PortfolioSignalWorker.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "PortfolioSignalWorker.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "PortfolioSignalWorker.dll"]