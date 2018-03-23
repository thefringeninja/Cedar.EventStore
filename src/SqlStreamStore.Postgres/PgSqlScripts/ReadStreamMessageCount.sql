CREATE OR REPLACE FUNCTION public.read_stream_message_count(
  _stream_id CHAR(42)
)
  RETURNS INT
AS $F$
DECLARE
  _stream_id_internal INT;
BEGIN
  SELECT public.streams.id_internal
  INTO _stream_id_internal
  FROM public.streams
  WHERE public.streams.id = _stream_id;

  SELECT count(*)
  FROM public.messages
  WHERE public.messages.stream_id_internal = _stream_id_internal;
END;
$F$
LANGUAGE 'plpgsql';