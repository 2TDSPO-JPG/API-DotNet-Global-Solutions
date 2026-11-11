namespace Saneia.Application.Monitoring;

internal static class ReadingValidation
{
    public static string? Validate(ReadingCommand input, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(input.DeviceId) || input.DeviceId.Length > 80 || input.DeviceId.Trim() != input.DeviceId)
            return "Informe deviceId sem espaços nas extremidades, com até 80 caracteres.";
        if (string.IsNullOrWhiteSpace(input.SampleId) || input.SampleId.Length > 80 || input.SampleId.Trim() != input.SampleId)
            return "Informe sampleId sem espaços nas extremidades, com até 80 caracteres.";
        if (input.At < DateTimeOffset.UnixEpoch || input.At > now.AddMinutes(1))
            return "Informe uma data de medição válida e não mais que um minuto no futuro.";
        if (input.Volume < 0 || input.Volume > 999999999m)
            return "O volume deve estar entre 0 e 999999999 litros.";
        if (input.LevelCm is < 0 or > 999999999m)
            return "O nível deve estar entre 0 e 999999999 centímetros.";
        if (decimal.Round(input.Volume, 18) != input.Volume ||
            input.LevelCm is { } level && decimal.Round(level, 18) != level)
            return "As medições aceitam até 18 casas decimais.";
        if (input.Note?.Length > 300)
            return "A observação deve ter até 300 caracteres.";
        return null;
    }
}
