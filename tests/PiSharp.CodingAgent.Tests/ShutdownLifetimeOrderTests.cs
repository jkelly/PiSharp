// Held focused controls reuse the unchanged actual postcommit/publisher fixtures.
internal static class ShutdownLifetimeOrderTests
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("shutdown-order. actual switch postcommit notification cancels before mutation join",
            SessionReplacementTests.Cases().Single(test => test.Name ==
                "session-replacement shutdown cancels a postcommit lifecycle callback before joining transition").Run),
        ("shutdown-order. actual create Abort and Dispose postcommit cleanup retain authority and joins",
            SessionCreationTests.Cases().Single(test => test.Name ==
                "session-creation postcommit abort and disposal join held notification cleanup").Run),
        ("shutdown-order. genuine action publisher acknowledgment precedes final lifetime cancellation",
            OwnedResourceRetirementTests.Cases().Single(test => test.Name ==
                "owned-resource. held publication joins before shutdown attachment cancellation").Run),
    ];
}
