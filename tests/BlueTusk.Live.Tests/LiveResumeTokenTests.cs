namespace BlueTusk.Live.Tests;

public sealed class LiveResumeTokenTests
{
    [Fact]
    public void Token_is_signed_expiring_versioned_and_bound_to_subscription()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        var protector = new LiveResumeTokenProtector(
            [
                new LiveResumeTokenKey("old", Enumerable.Repeat((byte)1, 32).ToArray()),
                new LiveResumeTokenKey("current", Enumerable.Repeat((byte)2, 32).ToArray(), isPrimary: true),
            ],
            time);
        var identity = Identity("scope:a");
        var token = protector.Protect(identity, 42, TimeSpan.FromMinutes(5));

        var valid = protector.Validate(token, identity);
        Assert.Equal(LiveResumeTokenValidationStatus.Valid, valid.Status);
        Assert.Equal(42, valid.Position!.Sequence);
        Assert.Equal(time.GetUtcNow().AddMinutes(5), valid.Position.ExpiresAt);

        Assert.Equal(
            LiveResumeTokenValidationStatus.IdentityMismatch,
            protector.Validate(token, Identity("scope:b")).Status);

        var tampered = token[..^1] + (token[^1] == 'A' ? 'B' : 'A');
        Assert.Equal(
            LiveResumeTokenValidationStatus.InvalidSignature,
            protector.Validate(tampered, identity).Status);

        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(
            LiveResumeTokenValidationStatus.Expired,
            protector.Validate(token, identity).Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("bt1.!!!!.!!!!")]
    public void Malformed_tokens_do_not_throw_or_authenticate(string token)
    {
        var protector = new LiveResumeTokenProtector(
            [new LiveResumeTokenKey("key", new byte[32], isPrimary: true)]);
        if (token.Length == 0)
        {
            Assert.Throws<ArgumentException>(() => protector.Validate(token, Identity("scope")));
            return;
        }

        Assert.Equal(
            LiveResumeTokenValidationStatus.Malformed,
            protector.Validate(token, Identity("scope")).Status);
    }

    private static LiveSubscriptionIdentity Identity(string scope) =>
        new(
            "database",
            new string('a', 64),
            new string('b', 64),
            scope,
            "policy:v1",
            50);

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
