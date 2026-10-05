namespace PipelineClip.Core;

public class HttpClientOptions
{
    public string BaseAddress { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 30;
    public string? ApiKey { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
}

public class GitLabOptions : HttpClientOptions
{
    public string ProjectId { get; set; } = "";
    public long PipelineId { get; set; }   // pipeline z błędem; argument CLI nadpisuje
    public bool IncludeAllowedFailures { get; set; } = false;
}

public class TestStageAnalysisOptions : HttpClientOptions
{
    public string Model { get; set; } = "";

    public string SystemPrompt { get; set; } =
        "Jesteś asystentem CI. Dostajesz fragmenty logu nieudanego joba testowego. " +
        "Podaj: 1) krótkie podsumowanie błędu, 2) prawdopodobną przyczynę, 3) sugestie naprawy. " +
        "Odpowiadaj po polsku, zwięźle. Nie zmyślaj, jeśli log nie wystarcza, napisz to.";

    public double Temperature { get; set; } = 0.1;
    public int MaxTokens { get; set; } = 800;
}

public class PipelineMergeOptions : HttpClientOptions
{
    public bool Enabled { get; set; } = false;
    public string Model { get; set; } = "";

    public string SystemPrompt { get; set; } =
        "Dostajesz podsumowania błędów z wielu jobów testowych jednego pipeline. " +
        "Scal je w jedno krótkie podsumowanie. Jeśli jest wspólna przyczyna, wskaż ją. " +
        "Odpowiadaj po polsku, zwięźle. Nie zmyślaj, opieraj się wyłącznie na podanych podsumowaniach.";

    public double Temperature { get; set; } = 0.1;
    public int MaxTokens { get; set; } = 800;
}

public class AnalysisOptions
{
    public int MaxLogBytes { get; set; } = 2_097_152;
    public int ChunkLines { get; set; } = 40;
    public int ChunkOverlapLines { get; set; } = 10;
    public int RetrieverCandidates { get; set; } = 40;
    public int MaxChunks { get; set; } = 20;
    public int MaxParallelJobs { get; set; } = 4;
    public string TargetStage { get; set; } = "test";
}
