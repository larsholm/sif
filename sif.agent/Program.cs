using sif.agent;

var app = new AgentApp();
ContextStore.CleanupPreviousSessions();
try
{
    return await app.Run(args);
}
finally
{
    ContextStore.CleanupCurrentSession();
}
