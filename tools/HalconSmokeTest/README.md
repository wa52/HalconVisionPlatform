# HALCON runtime probe

Standalone console probe for the local HALCON/.NET installation. It does not modify HALCON installation files, license files, environment variables, registry settings, or the inspection platform application.

## Run

```powershell
dotnet restore .\tools\HalconSmokeTest\HalconSmokeTest.csproj
dotnet run --project .\tools\HalconSmokeTest\HalconSmokeTest.csproj
```

With no argument, it reads `%HALCONIMAGES%\a01.png`. Pass another image path as the first argument to probe it instead. The process reports its managed interface assembly, loaded HALCON-root modules, versions, bitness, environment, each operator step, and the full exception on failure.

The captured run is in `docs/evidence/halcon-smoke-test-20261011.txt` and summarized in `docs/evidence/HALCON_SMOKE_TEST.md`.
