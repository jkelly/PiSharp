namespace Fixture.Private;

public static class Value
{
#if PRIVATE_TWO
    public static string Text => "two";
#else
    public static string Text => "one";
#endif
}
