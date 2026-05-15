#!/usr/bin/env python3
"""
Test script to display LSL stream metadata (descriptions and JSON schemas).
"""

try:
    from pylsl import resolve_byprop
    print("✅ pylsl imported successfully\n")
except ImportError as e:
    print("❌ pylsl not found. Install with: pip install pylsl")
    print(e)
    exit(1)

def print_stream_info(stream_name):
    """Print detailed info about a stream including metadata."""
    print(f"🔍 Searching for {stream_name}...")

    try:
        streams = resolve_byprop('name', stream_name, timeout=3.0)

        if not streams:
            print(f"❌ {stream_name} not found\n")
            return

        info = streams[0]
        print(f"\n{'='*70}")
        print(f"Stream: {info.name()}")
        print(f"{'='*70}")
        print(f"Type: {info.type()}")
        print(f"Channels: {info.channel_count()}")
        print(f"Sample Rate: {info.nominal_srate()} Hz")
        print(f"Format: {info.channel_format()}")
        print(f"Source ID: {info.source_id()}")

        # Get initial state from metadata
        desc = info.desc()
        initial_state_node = desc.child("initial_state")
        if initial_state_node and initial_state_node.child_value():
            print(f"\nInitial State:")
            print(f"  {initial_state_node.child_value()}")

        print()

    except Exception as e:
        print(f"❌ Error: {e}\n")

def main():
    print("=" * 70)
    print("VHI LSL Stream Metadata Viewer")
    print("=" * 70)
    print()

    # Check all VHI streams
    stream_names = ['VHI_Control', 'VHI_Predict']

    for stream_name in stream_names:
        print_stream_info(stream_name)

    print("=" * 70)
    print("Done!")
    print("=" * 70)

if __name__ == "__main__":
    main()
