FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY Soup.sln ./
COPY src/Soup.Api/Soup.Api.csproj src/Soup.Api/
RUN dotnet restore src/Soup.Api/Soup.Api.csproj
COPY src/Soup.Api/ src/Soup.Api/
RUN dotnet publish src/Soup.Api/Soup.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Soup.Api.dll"]
