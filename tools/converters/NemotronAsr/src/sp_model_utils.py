"""Minimal SentencePiece model writer — no protobuf compilation needed.

Encodes SentencePiece ModelProto directly using protobuf wire format.
"""

import struct
from typing import List, Tuple


def encode_varint(value: int) -> bytes:
    """Encode an integer as a protobuf varint."""
    result = []
    while value > 0x7F:
        result.append((value & 0x7F) | 0x80)
        value >>= 7
    result.append(value & 0x7F)
    return bytes(result)


def encode_length_delimited(data: bytes) -> bytes:
    """Encode a length-delimited protobuf field."""
    return encode_varint(len(data)) + data


def encode_float32(value: float) -> bytes:
    """Encode a float as protobuf fixed32 (little-endian)."""
    return struct.pack('<f', value)


def make_tag(field_number: int, wire_type: int) -> int:
    """Create a protobuf tag: (field_number << 3) | wire_type."""
    return (field_number << 3) | wire_type


# Wire types
WIRE_VARINT = 0
WIRE_FIXED64 = 1
WIRE_LENGTH_DELIMITED = 2
WIRE_FIXED32 = 5


def build_sp_piece(piece: str, score: float, piece_type: int) -> bytes:
    """Build a SentencePiece proto message (field 1 of ModelProto).

    message SentencePiece {
      optional string piece = 1;      // wire_type 2
      optional float score = 2;       // wire_type 5
      optional Type type = 3;         // wire_type 0 (enum = varint)
    }
    """
    result = b''
    # Field 1: piece (string)
    piece_bytes = piece.encode('utf-8')
    result += encode_varint(make_tag(1, WIRE_LENGTH_DELIMITED))
    result += encode_length_delimited(piece_bytes)
    # Field 2: score (float)
    result += encode_varint(make_tag(2, WIRE_FIXED32))
    result += encode_float32(score)
    # Field 3: type (enum, stored as varint)
    if piece_type != 0:
        result += encode_varint(make_tag(3, WIRE_VARINT))
        result += encode_varint(piece_type)
    return result


def build_sp_model(pieces_data: List[Tuple[str, float, int]],
                   model_type: str = 'bpe',
                   normalizer: str = 'identity') -> bytes:
    """Build a complete SentencePiece ModelProto.

    message ModelProto {
      repeated SentencePiece pieces = 1;      // wire_type 2
      optional TrainerSpec trainer_spec = 2;   // wire_type 2
      optional NormalizerSpec normalizer_spec = 3; // wire_type 2
    }
    """
    # Build pieces
    pieces_bytes = b''
    for piece, score, ptype in pieces_data:
        piece_msg = build_sp_piece(piece, score, ptype)
        pieces_bytes += encode_varint(make_tag(1, WIRE_LENGTH_DELIMITED))
        pieces_bytes += encode_length_delimited(piece_msg)

    # Build TrainerSpec { model_type = 3 }
    mt_bytes = model_type.encode('utf-8')
    trainer_spec = b''
    trainer_spec += encode_varint(make_tag(3, WIRE_LENGTH_DELIMITED))
    trainer_spec += encode_length_delimited(mt_bytes)

    pieces_bytes += encode_varint(make_tag(2, WIRE_LENGTH_DELIMITED))
    pieces_bytes += encode_length_delimited(trainer_spec)

    # Build NormalizerSpec { name = 1 }
    norm_bytes = normalizer.encode('utf-8')
    normalizer_spec = b''
    normalizer_spec += encode_varint(make_tag(1, WIRE_LENGTH_DELIMITED))
    normalizer_spec += encode_length_delimited(norm_bytes)

    pieces_bytes += encode_varint(make_tag(3, WIRE_LENGTH_DELIMITED))
    pieces_bytes += encode_length_delimited(normalizer_spec)

    return pieces_bytes


def parse_sp_model_raw(data: bytes) -> List[Tuple[str, float, int]]:
    """Parse SentencePiece ModelProto without protobuf library.

    Returns list of (piece, score, type) tuples.
    """
    pieces = []
    pos = 0
    while pos < len(data):
        tag, pos = _read_varint(data, pos)
        field_number = tag >> 3
        wire_type = tag & 0x07

        if wire_type == WIRE_VARINT:
            _, pos = _read_varint(data, pos)
        elif wire_type == WIRE_LENGTH_DELIMITED:
            length, pos = _read_varint(data, pos)
            sub_data = data[pos:pos + length]
            pos += length

            if field_number == 1:  # SentencePiece
                piece, score, ptype = _parse_sp_piece(sub_data)
                pieces.append((piece, score, ptype))
            # Fields 2 (trainer_spec) and 3 (normalizer_spec) are ignored
        elif wire_type == WIRE_FIXED32:
            pos += 4
        elif wire_type == WIRE_FIXED64:
            pos += 8
        else:
            raise ValueError(f"Unknown wire type: {wire_type}")
    return pieces


def _read_varint(data: bytes, pos: int):
    """Read a varint from protobuf data."""
    result = 0
    shift = 0
    while True:
        if pos >= len(data):
            raise ValueError("Unexpected end of data")
        byte = data[pos]
        pos += 1
        result |= (byte & 0x7F) << shift
        shift += 7
        if not (byte & 0x80):
            break
    return result, pos


def _parse_sp_piece(data: bytes):
    """Parse a SentencePiece sub-message."""
    piece = ''
    score = 0.0
    ptype = 1  # NORMAL
    pos = 0
    while pos < len(data):
        tag, pos = _read_varint(data, pos)
        field_number = tag >> 3
        wire_type = tag & 0x07

        if wire_type == WIRE_VARINT:
            val, pos = _read_varint(data, pos)
            if field_number == 3:
                ptype = val
        elif wire_type == WIRE_LENGTH_DELIMITED:
            length, pos = _read_varint(data, pos)
            if field_number == 1:
                piece = data[pos:pos + length].decode('utf-8')
            pos += length
        elif wire_type == WIRE_FIXED32:
            if field_number == 2:
                score = struct.unpack('<f', data[pos:pos + 4])[0]
            pos += 4
        else:
            break
    return piece, score, ptype
