# Store Resolver Metadata on Track Rows

Accepted plan: metadata lives as JSONB on `resolver_tracks`; legacy metadata rows are discarded; audio is permanent; metadata-only rows, lazy request-driven fill, sanitized yt-dlp JSON, and shared Harmony-shaped responses are used.
