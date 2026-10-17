using HomePodCast.ProtocolHarness;

namespace HomePodCast.Tests;

public class PcmCaptureBufferTests
{
    [Fact]
    public void Construction_rejects_a_missing_marker_or_capacity_that_cannot_hold_it()
    {
        Assert.Throws<ArgumentNullException>(() => new PcmCaptureBuffer(null!, 16));
        Assert.Throws<ArgumentException>(() => new PcmCaptureBuffer([], 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PcmCaptureBuffer([1, 2], -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PcmCaptureBuffer([1, 2], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PcmCaptureBuffer([1, 2], 1));
    }

    [Fact]
    public void An_empty_capture_and_empty_appends_have_no_marker_observation()
    {
        var capture = new PcmCaptureBuffer([1, 2], 2);
        capture.Append([], 123);
        Assert.Equal(0, capture.Count);
        Assert.True(capture.Written.IsEmpty);
        Assert.Equal(-1, capture.MarkerOffset);
        Assert.Equal(0, capture.MarkerArrivedAt);
        capture.Append([1, 2], 456);
        capture.Append([], 789);
        Assert.Equal(2, capture.Count);
        Assert.Equal(0, capture.MarkerOffset);
        Assert.Equal(456, capture.MarkerArrivedAt);
    }

    [Fact]
    public void Every_split_of_a_512_byte_marker_preserves_the_absolute_offset_and_completion_time()
    {
        var marker = new byte[512];
        for (int i = 0; i < marker.Length; i++) marker[i] = (byte)(i % 251 + 1);
        var signal = new byte[37 + marker.Length + 13];
        marker.CopyTo(signal, 37);
        // Include both endpoints: a complete marker in either read, as well as every crossing between bytes.
        for (int split = 0; split <= marker.Length; split++)
        {
            var capture = new PcmCaptureBuffer(marker, signal.Length);
            capture.Append(signal.AsSpan(0, 37 + split), 1111);
            Assert.Equal(split == marker.Length ? 37 : -1, capture.MarkerOffset);
            capture.Append(signal.AsSpan(37 + split), 2222);
            Assert.Equal(signal.Length, capture.Count);
            Assert.True(capture.Written.SequenceEqual(signal));
            Assert.Equal(37, capture.MarkerOffset);
            Assert.Equal(split == marker.Length ? 1111 : 2222, capture.MarkerArrivedAt);
        }
    }

    [Fact]
    public void A_marker_can_cross_many_reads_after_overlapping_incomplete_prefixes()
    {
        byte[] signal = [9, 1, 2, 1, 2, 1, 2, 1, 3, 9];
        var capture = new PcmCaptureBuffer([1, 2, 1, 2, 1, 3], signal.Length);
        for (int i = 0; i < signal.Length; i++)
        {
            capture.Append(signal.AsSpan(i, 1), 1000 + i);
            Assert.Equal(i < 8 ? -1 : 3, capture.MarkerOffset);
        }
        Assert.Equal(1008, capture.MarkerArrivedAt);
        Assert.True(capture.Written.SequenceEqual(signal));
    }

    [Fact]
    public void False_overlapping_prefixes_and_later_matches_keep_the_first_observation()
    {
        var capture = new PcmCaptureBuffer([1, 1, 1, 2], 12);
        capture.Append([1, 1, 1], 100);
        capture.Append([1], 200);
        Assert.Equal(-1, capture.MarkerOffset);
        capture.Append([1, 2, 1, 1, 1, 2], 300);
        Assert.Equal(2, capture.MarkerOffset);
        Assert.Equal(300, capture.MarkerArrivedAt);
        capture.Append([1, 2], 400);
        Assert.Equal(2, capture.MarkerOffset);
        Assert.Equal(300, capture.MarkerArrivedAt);
        Assert.True(capture.Written.SequenceEqual(new byte[] { 1, 1, 1, 1, 1, 2, 1, 1, 1, 2, 1, 2 }));
    }

    [Fact]
    public void Overlapping_complete_matches_use_the_earliest_absolute_offset()
    {
        var capture = new PcmCaptureBuffer([1, 2, 1], 8);
        capture.Append([9, 1, 2], 100);
        Assert.Equal(-1, capture.MarkerOffset);
        capture.Append([1, 2, 1], 200); // complete matches begin at offsets 1 and 3
        Assert.Equal(1, capture.MarkerOffset);
        Assert.Equal(200, capture.MarkerArrivedAt);
        capture.Append([2, 1], 300);
        Assert.Equal(1, capture.MarkerOffset);
        Assert.Equal(200, capture.MarkerArrivedAt);
        Assert.True(capture.Written.SequenceEqual(new byte[] { 9, 1, 2, 1, 2, 1, 2, 1 }));
    }

    [Fact]
    public void The_marker_is_owned_by_the_capture_even_if_the_callers_array_changes()
    {
        byte[] marker = [1, 2, 3];
        var capture = new PcmCaptureBuffer(marker, 4);
        marker[0] = 99;
        capture.Append([0, 1, 2, 3], 123);
        Assert.Equal(1, capture.MarkerOffset);
        Assert.Equal(123, capture.MarkerArrivedAt);
    }

    [Fact]
    public void Recording_past_85000_bytes_keeps_every_byte_and_finds_a_later_marker()
    {
        var marker = new byte[512];
        for (int i = 0; i < marker.Length; i++) marker[i] = (byte)(251 + i % 5);
        var signal = new byte[200000];
        for (int i = 0; i < signal.Length; i++) signal[i] = (byte)((i * 73 + 19) % 251);
        marker.CopyTo(signal, 90123);
        var capture = new PcmCaptureBuffer(marker, signal.Length);
        for (int start = 0; start < signal.Length; start += 8192)
            capture.Append(signal.AsSpan(start, Math.Min(8192, signal.Length - start)), 1000 + start);
        Assert.Equal(signal.Length, capture.Count);
        Assert.Equal(90123, capture.MarkerOffset);
        Assert.Equal(91112, capture.MarkerArrivedAt); // the twelfth read completes the marker
        Assert.True(capture.Written.SequenceEqual(signal));
    }

    [Fact]
    public void Overflow_rejects_the_whole_block_without_truncating_or_observing_its_marker()
    {
        var capture = new PcmCaptureBuffer([7, 8], 6);
        capture.Append([1, 2, 7], 100);
        Assert.Throws<InvalidOperationException>(() => capture.Append([8, 9, 10, 11], 200));
        Assert.Equal(3, capture.Count);
        Assert.True(capture.Written.SequenceEqual(new byte[] { 1, 2, 7 }));
        Assert.Equal(-1, capture.MarkerOffset);
        Assert.Equal(0, capture.MarkerArrivedAt);

        capture.Append([8, 9, 10], 300);
        Assert.Throws<InvalidOperationException>(() => capture.Append([11], 400));
        capture.Append([], 500);
        Assert.Equal(6, capture.Count);
        Assert.True(capture.Written.SequenceEqual(new byte[] { 1, 2, 7, 8, 9, 10 }));
        Assert.Equal(2, capture.MarkerOffset);
        Assert.Equal(300, capture.MarkerArrivedAt);
    }

    [Fact]
    public void Appending_searching_and_the_first_marker_observation_allocate_nothing()
    {
        byte[] marker = [241, 226, 211, 196];
        var block = new byte[128];
        const int blocks = 1000;
        int capacity = (blocks + 1) * block.Length + marker.Length;
        // Signals.Allocated invokes the action twice. Both complete recordings are allocated before its warmup,
        // so the measured pass includes searching, the first successful match, and appending after that match.
        PcmCaptureBuffer[] captures = [new(marker, capacity), new(marker, capacity)];
        int next = 0;
        long bytes = Signals.Allocated(() =>
        {
            var capture = captures[next++];
            for (int i = 0; i < blocks; i++) capture.Append(block, i);
            capture.Append(marker, 12345);
            capture.Append(block, 23456);
        });
        Assert.Equal(0, bytes);
        foreach (var capture in captures)
        {
            Assert.Equal(capacity, capture.Count);
            Assert.Equal(blocks * block.Length, capture.MarkerOffset);
            Assert.Equal(12345, capture.MarkerArrivedAt);
        }
    }
}
