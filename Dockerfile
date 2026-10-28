FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/AgentKnowledgeBase.Mcp/AgentKnowledgeBase.Mcp.csproj src/AgentKnowledgeBase.Mcp/
RUN dotnet restore src/AgentKnowledgeBase.Mcp/AgentKnowledgeBase.Mcp.csproj

COPY src/AgentKnowledgeBase.Mcp/ src/AgentKnowledgeBase.Mcp/
RUN dotnet publish src/AgentKnowledgeBase.Mcp/AgentKnowledgeBase.Mcp.csproj \
    -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080

USER app
ENTRYPOINT ["dotnet", "AgentKnowledgeBase.Mcp.dll"]
