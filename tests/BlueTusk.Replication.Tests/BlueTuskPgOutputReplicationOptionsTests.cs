namespace BlueTusk.Replication.Tests;

public sealed class BlueTuskPgOutputReplicationOptionsTests
{
    [Fact]
    public void Accepts_protocol_capabilities_at_their_minimum_versions()
    {
        new BlueTuskPgOutputReplicationOptions
        {
            SlotName = "slot",
            PublicationNames = ["publication"],
            ProtocolVersion = 2,
            StreamingMode = BlueTuskLogicalStreamingMode.On,
        }.Validate();
        new BlueTuskPgOutputReplicationOptions
        {
            SlotName = "slot",
            PublicationNames = ["publication"],
            ProtocolVersion = 3,
            TwoPhase = true,
        }.Validate();
        new BlueTuskPgOutputReplicationOptions
        {
            SlotName = "slot",
            PublicationNames = ["publication"],
            ProtocolVersion = 4,
            StreamingMode = BlueTuskLogicalStreamingMode.Parallel,
        }.Validate();
    }

    [Theory]
    [InlineData(1, BlueTuskLogicalStreamingMode.On, false)]
    [InlineData(3, BlueTuskLogicalStreamingMode.Parallel, false)]
    [InlineData(2, BlueTuskLogicalStreamingMode.Off, true)]
    public void Rejects_incompatible_protocol_capabilities(
        int protocolVersion,
        BlueTuskLogicalStreamingMode streamingMode,
        bool twoPhase)
    {
        var options = new BlueTuskPgOutputReplicationOptions
        {
            SlotName = "slot",
            PublicationNames = ["publication"],
            ProtocolVersion = protocolVersion,
            StreamingMode = streamingMode,
            TwoPhase = twoPhase,
        };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void Requires_a_slot_and_at_least_one_publication()
    {
        Assert.Throws<ArgumentException>(
            new BlueTuskPgOutputReplicationOptions
            {
                SlotName = "",
                PublicationNames = ["publication"],
            }.Validate);
        Assert.Throws<ArgumentException>(
            new BlueTuskPgOutputReplicationOptions
            {
                SlotName = "slot",
                PublicationNames = [],
            }.Validate);
    }
}
