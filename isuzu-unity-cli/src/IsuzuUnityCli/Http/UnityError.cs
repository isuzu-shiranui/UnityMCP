namespace IsuzuUnityCli.Http;

/// <summary>A failed exchange with the Editor, carrying the code and message from its error envelope when there was one.</summary>
public sealed class UnityError : Exception
{
    public string Code { get; }
    public int? HttpStatus { get; }

    public UnityError(string code, string message, int? httpStatus = null, Exception? inner = null) : base(message, inner)
    {
        Code = code;
        HttpStatus = httpStatus;
    }
}
