# 사용량 API 응답 스키마

`GET https://api.anthropic.com/api/oauth/usage`

헤더:
- `Authorization: Bearer <access_token>`
- `anthropic-beta: oauth-2025-04-20`

비공식 엔드포인트라 필드 구성이 예고 없이 바뀔 수 있다. 아래는 이 앱이 실제로 읽는 필드만 추린 것이며,
값은 전부 예시다. 파서는 모르는 필드를 무시하고, 없는 값은 UI에서 자동으로 숨기도록 작성되어 있다.

```jsonc
{
  // 5시간 세션 한도
  "five_hour": {
    "utilization": 0,                                // %
    "resets_at": "2026-01-01T00:00:00.000000+00:00"
  },

  // 주간 한도 (전체 모델)
  "seven_day": {
    "utilization": 0,
    "resets_at": "2026-01-01T00:00:00.000000+00:00"
  },

  // 한도 목록 — 모델 전용 주간 한도는 여기에서만 확인된다
  "limits": [
    { "kind": "session",     "group": "session", "percent": 0, "resets_at": "...", "scope": null },
    { "kind": "weekly_all",  "group": "weekly",  "percent": 0, "resets_at": "...", "scope": null },
    {
      "kind": "weekly_scoped",
      "group": "weekly",
      "percent": 0,
      "resets_at": "...",
      "scope": { "model": { "id": null, "display_name": "<모델명>" }, "surface": null }
    }
  ],

  // 추가 사용량 크레딧 (요약)
  "extra_usage": {
    "is_enabled": false,
    "utilization": 0,
    "currency": "USD"
  },

  // 추가 사용량 크레딧 (금액 상세) — 사이드바의 C 지표가 우선 사용
  "spend": {
    "used":  { "amount_minor": 0, "currency": "USD", "exponent": 2 },  // 센트 단위
    "limit": { "amount_minor": 0, "currency": "USD", "exponent": 2 },
    "percent": 0,
    "enabled": false,
    "disabled_reason": null
  }
}
```

## 파싱 시 주의점

- **모델 전용 주간 한도(Fable 등)는 최상위 `seven_day_<model>` 필드가 아니라 `limits[]` 안에 있다.**
  최상위에 비슷한 이름의 필드가 있더라도 `null`인 경우가 많으므로 `limits[]`를 우선 확인한다.
- `spend.used/limit`은 **센트 단위 정수**다. `exponent`만큼 나눠야 실제 금액이 된다.
- 이 엔드포인트와 토큰 갱신 엔드포인트 모두 **HTTP 429**를 반환할 수 있다. 짧은 간격으로 반복 호출하면
  한동안 전체 차단되므로 백오프가 필요하다.
