internal static class Fixtures
{
    // Synthetic public-schema example, NOT captured from a private/live Form.
    internal const string DescribedChoice = """
        {"id":"form-public-example","sessionID":"session-public-example","fields":[
          {"key":"decision","type":"string","required":true,"minLength":3,"maxLength":8,
           "placeholder":"Choose explicitly","default":"yes",
           "options":[{"value":"yes","label":"Yes","description":"Continue the task"},
                      {"value":"no","label":"No"}]}]}
        """;
}
