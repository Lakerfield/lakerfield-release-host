# .NET SDK
# https://github.com/dotnet/dotnet-docker/blob/main/README.sdk.md#full-tag-listing
# 10.0.403-alpine3.24
#
# ASP.NET Core Runtime
# https://github.com/dotnet/dotnet-docker/blob/main/README.aspnet.md#full-tag-listing
# 10.0.14-alpine3.24

FROM mcr.microsoft.com/dotnet/sdk:10.0.403-alpine3.24 AS build
WORKDIR /src
COPY ./src/Lakerfield.ReleaseHost/ .
RUN dotnet publish -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-alpine3.24
WORKDIR /app
COPY --from=build /out .
VOLUME ["/data/files"]
ENV ASPNETCORE_URLS=http://+:80
EXPOSE 80
ENTRYPOINT ["dotnet", "Lakerfield.ReleaseHost.dll"]
