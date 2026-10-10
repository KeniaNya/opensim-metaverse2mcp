namespace Opensim.Metaverse2Mcp;

internal sealed record CameraState(
    float CameraX,
    float CameraY,
    float CameraZ,
    float AtAxisX,
    float AtAxisY,
    float AtAxisZ,
    float LeftAxisX,
    float LeftAxisY,
    float LeftAxisZ,
    float UpAxisX,
    float UpAxisY,
    float UpAxisZ,
    float Far,
    float AgentX,
    float AgentY,
    float AgentZ);

internal sealed record CameraStateResult(bool Ok, string Message, CameraState? State)
{
    public static CameraStateResult FailResult(string message) => new(false, message, null);
}
