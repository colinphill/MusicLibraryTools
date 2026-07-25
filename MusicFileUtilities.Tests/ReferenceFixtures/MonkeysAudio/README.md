# Monkey's Audio reference fixture

`sample.ape` is a deterministic 0.3-second, 440 Hz, 44.1 kHz, stereo tone
encoded at the normal compression level with the official Monkey's Audio
13.20 SDK console tool.

The SDK was downloaded from the official Monkey's Audio developer page and is
licensed under the 3-clause BSD license. The encoder executable and SDK are not
committed or distributed. The generated audio stream is copied into each test
output and the fixture generator appends the shared baseline APEv2 fields.

The reference stream passes both the official console verifier and FFmpeg
decoding. Its SHA-256 is:

`7FA235D829A2B30C152C27052993854C0AC06F938180597158E832D8D5AFFDA4`
