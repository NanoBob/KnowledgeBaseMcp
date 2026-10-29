@echo off
setlocal

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
    exit /b 1
)

set IMAGE=agent-knowledge-base-mcp
set TAG=%~1
if "%TAG%"=="" set TAG=latest

set REMOTE=%AGENT_KNOWLEDGE_BASE_REGISTRY_IMAGE%:%TAG%

docker image inspect %IMAGE%:latest >nul 2>&1
if errorlevel 1 (
    echo Image %IMAGE%:latest not found. Build it first with: docker compose build mcp
    exit /b 1
)

echo Publishing %IMAGE%:latest as %REMOTE%
docker tag %IMAGE%:latest %REMOTE%
if errorlevel 1 exit /b 1

docker push %REMOTE%
if errorlevel 1 exit /b 1
