If (Test-Path "runprebuild.bat") {
    ./runprebuild.bat
}

nuget restore Halcyon.sln

msbuild
