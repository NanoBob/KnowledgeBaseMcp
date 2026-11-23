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
set FRONTEND_IMAGE=%IMAGE%-frontend
set TAG=latest
set BUILD=1
set PUBLISH_API=1
set PUBLISH_FRONTEND=0

set "FRONTEND_BUILD_ARGS="
if defined FRONTEND_API_BASE set "FRONTEND_BUILD_ARGS=--build-arg VITE_API_BASE=%FRONTEND_API_BASE%"

:parse
if "%~1"=="" goto parsed
if /i "%~1"=="--no-build" goto arg_no_build
if /i "%~1"=="--api" goto arg_api
if /i "%~1"=="--frontend" goto arg_frontend
if /i "%~1"=="--all" goto arg_all
set TAG=%~1
shift
goto parse

:arg_no_build
set BUILD=0
shift
goto parse

:arg_api
set PUBLISH_API=1
set PUBLISH_FRONTEND=0
shift
goto parse

:arg_frontend
set PUBLISH_API=0
set PUBLISH_FRONTEND=1
shift
goto parse

:arg_all
set PUBLISH_API=1
set PUBLISH_FRONTEND=1
shift
goto parse

:parsed

if "%PUBLISH_API%"=="1" (
    call :publish "%IMAGE%" "%AGENT_KNOWLEDGE_BASE_REGISTRY_IMAGE%" "Dockerfile" "." ""
    if errorlevel 1 goto fail
)

if "%PUBLISH_FRONTEND%"=="1" (
    call :publish "%FRONTEND_IMAGE%" "%AGENT_KNOWLEDGE_BASE_REGISTRY_IMAGE%-frontend" "Frontend/Dockerfile" "Frontend" "%FRONTEND_BUILD_ARGS%"
    if errorlevel 1 goto fail
)

popd >nul
exit /b 0

:publish
set "PKG_IMAGE=%~1"
set "PKG_REGISTRY=%~2"
set "PKG_DOCKERFILE=%~3"
set "PKG_CONTEXT=%~4"
set "PKG_ARGS=%~5"

if "%BUILD%"=="1" (
    echo Building %PKG_IMAGE%:latest from %PKG_DOCKERFILE%...
    docker build -f "%PKG_DOCKERFILE%" -t "%PKG_IMAGE%:latest" %PKG_ARGS% "%PKG_CONTEXT%"
    if errorlevel 1 exit /b 1
)

docker image inspect "%PKG_IMAGE%:latest" >nul 2>&1
if errorlevel 1 (
    echo Image %PKG_IMAGE%:latest not found. Run again without --no-build to build it.
    exit /b 1
)

echo Publishing %PKG_IMAGE%:latest as %PKG_REGISTRY%:%TAG%
docker tag "%PKG_IMAGE%:latest" "%PKG_REGISTRY%:%TAG%"
if errorlevel 1 exit /b 1

docker push "%PKG_REGISTRY%:%TAG%"
if errorlevel 1 exit /b 1

exit /b 0

:fail
popd >nul
exit /b 1
