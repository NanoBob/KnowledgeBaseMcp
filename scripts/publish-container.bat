@echo off
setlocal

pushd "%~dp0.." >nul

rem Target image without a tag. Set it in the environment, or add a line such as
rem AGENT_KNOWLEDGE_BASE_REGISTRY_IMAGE=registry.example.com/namespace/agent-knowledge-base-mcp
rem to the git-ignored .env file in the repository root.
if "%AGENT_KNOWLEDGE_BASE_REGISTRY_IMAGE%"=="" (
    if exist "%~dp0..\.env" (
        for /f "usebackq eol=# tokens=1,* delims==" %%A in ("%~dp0..\.env") do (
            if /i "%%A"=="AGENT_KNOWLEDGE_BASE_REGISTRY_IMAGE" set "AGENT_KNOWLEDGE_BASE_REGISTRY_IMAGE=%%B"
        )
    )
)

if "%AGENT_KNOWLEDGE_BASE_REGISTRY_IMAGE%"=="" (
    echo AGENT_KNOWLEDGE_BASE_REGISTRY_IMAGE is not set. Set it to the target image without a tag,
    echo for example registry.example.com/namespace/agent-knowledge-base-mcp, then run this script again.
    popd >nul
    exit /b 1
)

set IMAGE=agent-knowledge-base-mcp
set TAG=latest
set BUILD=1

:parse
if "%~1"=="" goto parsed
if /i "%~1"=="--no-build" (
    set BUILD=0
) else (
    set TAG=%~1
)
shift
goto parse
:parsed

if "%BUILD%"=="1" (
    echo Building %IMAGE%:latest from Dockerfile...
    docker build -f Dockerfile -t %IMAGE%:latest .
    if errorlevel 1 goto fail
)

docker image inspect %IMAGE%:latest >nul 2>&1
if errorlevel 1 (
    echo Image %IMAGE%:latest not found. Run again without --no-build to build it.
    goto fail
)

set REMOTE=%AGENT_KNOWLEDGE_BASE_REGISTRY_IMAGE%:%TAG%

echo Publishing %IMAGE%:latest as %REMOTE%
docker tag %IMAGE%:latest %REMOTE%
if errorlevel 1 goto fail

docker push %REMOTE%
if errorlevel 1 goto fail

popd >nul
exit /b 0

:fail
popd >nul
exit /b 1
