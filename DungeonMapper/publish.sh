
publish() {
    dotnet publish -c Release -r "$1" --self-contained true -p:PublishSingleFile=true  -p:IncludeNativeLibrariesForSelfExtract=true
}
# -p:PublishTrimmed=true # breaks reflection-based serialization used in JSON serialization.

publish "win-x64"
publish "linux-x64"