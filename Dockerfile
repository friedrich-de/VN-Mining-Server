# syntax=docker/dockerfile:1

# The page, built first: the server carries it inside itself.
FROM node:24-slim AS page
WORKDIR /src
# The page reads the server's version from here.
COPY Directory.Build.props ./
COPY web/package.json web/package-lock.json web/
RUN cd web && npm ci
COPY web web
RUN cd web && npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS server
WORKDIR /src
COPY global.json Directory.Build.props .editorconfig ./
COPY VnMiningServer/VnMiningServer.csproj VnMiningServer/
RUN dotnet restore VnMiningServer/VnMiningServer.csproj
COPY VnMiningServer VnMiningServer
COPY --from=page /src/web/dist web/dist
RUN dotnet publish VnMiningServer/VnMiningServer.csproj -c Release --no-restore -o /out

# Alpine, because its ffmpeg comes with far less around it: the image is less than half the size.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
# ffmpeg joins the voices of lines and makes the pictures and sounds of cards.
RUN apk add --no-cache ffmpeg
WORKDIR /app
COPY --from=server /out .
# Everything the server keeps is in /data: the lines, the dictionaries, the settings and the API key.
RUN mkdir /data && chown "$APP_UID" /data
USER $APP_UID
ENV VN_MINING_DATA=/data \
    VN_MINING_LISTEN=http://0.0.0.0:8733
VOLUME /data
EXPOSE 8733
ENTRYPOINT ["dotnet", "VnMiningServer.dll"]
