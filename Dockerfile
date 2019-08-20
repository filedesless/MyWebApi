FROM mcr.microsoft.com/dotnet/core/sdk:2.1-alpine AS build-env
WORKDIR /app
COPY *.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish -c Release -o output

# Runtime image
FROM mcr.microsoft.com/dotnet/core/aspnet:2.1-alpine
WORKDIR /app
COPY --from=build-env /app/output .
ENTRYPOINT ["dotnet", "MyWebApi.dll"]