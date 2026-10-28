FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG VERSION=0.0.0.0
WORKDIR /src
COPY src/RelativeArtist.csproj ./
RUN dotnet restore
COPY src/ ./
RUN dotnet build -c Release --no-restore -p:Version=$VERSION -o /out

FROM scratch AS export
COPY --from=build /out/*.dll /
