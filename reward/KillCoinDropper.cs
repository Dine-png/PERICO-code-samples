using System.Collections;
using System.Collections.Generic;
using BugForgee.SteamNetworking;
using UnityEngine;

/// <summary>
/// 잡몹 처치 보상을 바닥 코인으로 떨어뜨리고, 주운 코인만 팀 자금에 넣는다.
/// KillRewardSystem 이 같은 오브젝트에 런타임으로 붙인다(씬 배선 없음).
///
/// 네트워크 흐름 — 코인마다 PhotonView 를 만들지 않고 이벤트 3개로 끝낸다.
///   1) 드랍: 마스터가 (첫 코인 ID, 위치, 개당 금액[], 시드)를 전원에게 보낸다 → 각자 분수처럼 한 닢씩 쏘아 올린다.
///   2) 줍기: 로컬 플레이어가 우클릭(집게 흡수, ScrapMagnet)으로 코인을 빨아들이면 마스터에게 코인 ID 를 보낸다.
///   3) 확정: 마스터가 아직 안 주운 코인인지 확인하고 AddScrap 후 전원에게 삭제를 알린다.
/// 금액은 요청이 아니라 마스터가 기억하는 값으로 지급한다 — 두 명이 동시에 먹어도 한 번만 들어간다.
/// </summary>
public class KillCoinDropper : MonoBehaviour, IOnEventCallback
{
    const byte DropEvent = 170;
    const byte PickupRequestEvent = 171;
    const byte CollectedEvent = 172;
    const string CoinResourcePath = "KillCoin/KillCoin";
    const string PickupClipResourcePath = "Audio/KillCoin/KillCoinPickup";
    // 코인 ID = 발행자 액터 번호 << ActorShift | 일련번호 — 마스터가 바뀌어도 ID 가 겹치지 않는다
    const int ActorShift = 20;
    // 몬스터가 삼킨 코인 — 줍기 소리 없이 그 자리에서 사라진다
    const int MonsterCollector = -1;

    [Header("코인 개수 (금액을 나눠 담는다)")]
    [SerializeField] private Vector2Int smallCoinCount = new Vector2Int(1, 3);
    [SerializeField] private Vector2Int mediumCoinCount = new Vector2Int(4, 6);

    [Header("분수처럼 솟구치기")]
    [Tooltip("위로 쏘아 올리는 속도(m/s)")]
    [SerializeField] private float ejectUpSpeed = 6.5f;
    [Tooltip("옆으로 퍼지는 속도(m/s) — 작을수록 곧게 솟는다")]
    [SerializeField] private float ejectSideSpeed = 1.3f;
    [Tooltip("한 닢씩 연달아 쏘는 간격(초)")]
    [SerializeField] private float ejectInterval = 0.07f;
    [Tooltip("솟구칠 때 도는 속도(rad/s)")]
    [SerializeField] private float ejectSpin = 14f;

    [Header("수명")]
    [Tooltip("안 주우면 이 시간(초) 뒤 사라진다")]
    [SerializeField] private float coinLifetime = 180f;
    [Tooltip("수명이 끝난 뒤에도 마스터가 줍기 요청을 받아주는 여유(초) — 클라이언트 간 타이머 오차 흡수")]
    [SerializeField] private float claimGrace = 2f;

    [Header("줍기 판정")]
    [Tooltip("집게 손 위치를 못 찾았을 때 쓰는 기준점 높이(m)")]
    [SerializeField] private float playerAnchorHeight = 0.9f;
    [SerializeField] private float localPlayerRescanInterval = 0.5f;

    [Header("줍기 사운드")]
    [SerializeField, Range(0f, 1f)] private float pickupVolume = 0.6f;
    [SerializeField] private float pickupPitchJitter = 0.1f;
    [SerializeField] private float pickupMaxDistance = 12f;

    private struct Claim
    {
        public int value;
        public float expireTime;
    }

    private readonly Dictionary<int, KillCoin> _coins = new Dictionary<int, KillCoin>();
    // 마스터 전용 — 아직 지급 안 한 코인의 금액. 클라이언트가 보낸 금액은 믿지 않는다.
    private readonly Dictionary<int, Claim> _claims = new Dictionary<int, Claim>();
    private readonly List<int> _expiredScratch = new List<int>();

    private KillCoin _coinPrefab;
    private KillCoinSparks _sparks;
    private AudioClip _pickupClip;
    private int _nextSerial;
    private Transform _localPlayer;
    private PlayerStats _localStats;
    private ScrapMagnet _localMagnet;
    private float _nextPlayerScan;
    private float _nextClaimSweep;

    public bool HasLocalAnchor => _localPlayer != null && _localPlayer.gameObject.activeInHierarchy
        && (_localStats == null || !_localStats.IsDead);
    public Vector3 LocalAnchor => _localMagnet != null && _localMagnet.handAnchor != null
        ? _localMagnet.handAnchor.position
        : _localPlayer.position + Vector3.up * playerAnchorHeight;

    /// <summary>로컬 플레이어가 우클릭 흡수를 누르고 있는가 — 이미 빨려오던 코인은 이 동안 계속 따라온다.</summary>
    public bool IsAbsorbHeld => HasLocalAnchor && _localMagnet != null && _localMagnet.IsHolding;

    /// <summary>우클릭 흡수 범위(집게와 같은 사거리·조준 각도) 안에 있는 코인인가.</summary>
    public bool CanStartAbsorb(Vector3 position)
    {
        if (!IsAbsorbHeld) return false;

        Transform cam = _localMagnet.cameraTransform;
        if (cam == null) return false;

        Vector3 to = position - cam.position;
        if (to.sqrMagnitude > _localMagnet.maxRange * _localMagnet.maxRange) return false;
        return Vector3.Angle(cam.forward, to) <= _localMagnet.coneHalfAngle;
    }

    void Awake()
    {
        GameObject prefab = Resources.Load<GameObject>(CoinResourcePath);
        _coinPrefab = prefab != null ? prefab.GetComponent<KillCoin>() : null;
        if (_coinPrefab == null)
            Debug.LogError($"[KillCoin] 코인 프리팹을 찾지 못했습니다: Resources/{CoinResourcePath}", this);

        _pickupClip = Resources.Load<AudioClip>(PickupClipResourcePath);
        _sparks = KillCoinSparks.Create(transform);
    }

    void OnEnable() => PhotonNetwork.AddCallbackTarget(this);
    void OnDisable() => PhotonNetwork.RemoveCallbackTarget(this);

    void Update()
    {
        if (_coins.Count == 0 && _claims.Count == 0) return;

        if (!HasLocalAnchor && Time.time >= _nextPlayerScan)
        {
            _nextPlayerScan = Time.time + localPlayerRescanInterval;
            FindLocalPlayer();
        }

        if (Time.time >= _nextClaimSweep)
        {
            _nextClaimSweep = Time.time + 1f;
            SweepExpiredClaims();
        }
    }

    /// <summary>
    /// 마스터 전용 — total 금액을 몬스터 등급에 맞는 개수로 나눠 position 에 뿌린다.
    /// 프리팹이 없으면 false — 호출부가 즉시 지급으로 대신한다.
    /// </summary>
    public bool Drop(Vector3 position, int total, MonsterRewardTier tier)
    {
        if (_coinPrefab == null || total <= 0) return false;

        Vector2Int range = tier == MonsterRewardTier.Medium ? mediumCoinCount : smallCoinCount;
        int count = Mathf.Clamp(Random.Range(range.x, range.y + 1), 1, total);
        int[] values = new int[count];
        for (int i = 0; i < count; i++)
            values[i] = total / count + (i < total % count ? 1 : 0);

        int actor = PhotonNetwork.InRoom ? PhotonNetwork.LocalPlayer.ActorNumber : 0;
        int firstId = (actor << ActorShift) | (_nextSerial & ((1 << ActorShift) - 1));
        _nextSerial += count;
        int seed = Random.Range(int.MinValue, int.MaxValue);

        if (!PhotonNetwork.InRoom)
        {
            SpawnCoins(firstId, position, values, seed);
            return true;
        }

        PhotonNetwork.RaiseEvent(
            DropEvent,
            new object[] { firstId, position, values, seed },
            new RaiseEventOptions { Receivers = ReceiverGroup.All },
            SendOptions.SendReliable);
        return true;
    }

    /// <summary>빨아들인 코인이 집게에 닿았다 — 화면에서는 바로 먹은 것으로 보이고 지급은 마스터가 확정한다.</summary>
    public void RequestPickup(KillCoin coin)
    {
        if (coin == null) return;

        int id = coin.Id;
        _coins.Remove(id);
        PlayPickupSound(coin.transform.position);
        coin.PlayCollected();

        if (!PhotonNetwork.InRoom)
        {
            if (_claims.Remove(id, out Claim claim))
                NetworkDataManager.Instance?.AddScrap(claim.value);
            return;
        }

        if (PhotonNetwork.IsMasterClient)
        {
            ConfirmPickup(id, PhotonNetwork.LocalPlayer.ActorNumber);
            return;
        }

        PhotonNetwork.RaiseEvent(
            PickupRequestEvent,
            id,
            new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient },
            SendOptions.SendReliable);
    }

    /// <summary>
    /// 마스터 전용 — from 에서 radius 안의 가장 가까운 코인 위치. 메모리 누수(MemoryLeakAI)가 먹으러 갈 때 쓴다.
    /// 코인은 클라이언트마다 물리를 따로 돌리므로 마스터 화면의 위치 기준이다.
    /// </summary>
    public bool TryFindNearestCoin(Vector3 from, float radius, out Vector3 position)
    {
        return TryFindNearestCoin(from, radius, out _, out position);
    }

    bool TryFindNearestCoin(Vector3 from, float radius, out int id, out Vector3 position)
    {
        id = 0;
        position = from;
        float best = radius * radius;
        bool found = false;
        foreach (KeyValuePair<int, KillCoin> pair in _coins)
        {
            KillCoin coin = pair.Value;
            if (coin == null || !_claims.ContainsKey(pair.Key)) continue;

            float sqr = (coin.transform.position - from).sqrMagnitude;
            if (sqr >= best) continue;
            best = sqr;
            id = pair.Key;
            position = coin.transform.position;
            found = true;
        }
        return found;
    }

    /// <summary>
    /// 마스터 전용 — radius 안의 코인 하나를 몬스터가 삼킨다. 돈은 아무에게도 들어가지 않고 전원 화면에서 사라진다.
    /// </summary>
    public bool TryConsumeCoin(Vector3 from, float radius) => TryConsumeCoin(from, radius, out _);

    /// <param name="value">삼킨 코인의 금액 — 로봇청소기가 배에 쌓아 두었다가 죽을 때 돌려준다</param>
    public bool TryConsumeCoin(Vector3 from, float radius, out int value)
    {
        value = 0;
        if (PhotonNetwork.InRoom && !PhotonNetwork.IsMasterClient) return false;
        if (!TryFindNearestCoin(from, radius, out int id, out _)) return false;

        if (_claims.TryGetValue(id, out Claim claim)) value = claim.value;
        _claims.Remove(id);
        if (!PhotonNetwork.InRoom)
        {
            HandleCollected(id, MonsterCollector);
            return true;
        }
        PhotonNetwork.RaiseEvent(
            CollectedEvent,
            new object[] { id, MonsterCollector },
            new RaiseEventOptions { Receivers = ReceiverGroup.All },
            SendOptions.SendReliable);
        return true;
    }

    /// <summary>수명이 끝난 코인이 스스로 사라질 때 목록에서 뺀다.</summary>
    public void Unregister(KillCoin coin)
    {
        if (coin != null && _coins.TryGetValue(coin.Id, out KillCoin registered) && registered == coin)
            _coins.Remove(coin.Id);
    }

    public void OnEvent(EventData photonEvent)
    {
        if (photonEvent == null) return;

        switch (photonEvent.Code)
        {
            case DropEvent:
                if (photonEvent.CustomData is object[] drop && drop.Length >= 4
                    && drop[0] is int firstId && drop[1] is Vector3 position
                    && drop[2] is int[] values && drop[3] is int seed)
                {
                    SpawnCoins(firstId, position, values, seed);
                }
                break;

            case PickupRequestEvent:
                if (PhotonNetwork.IsMasterClient && photonEvent.CustomData is int requestId)
                    ConfirmPickup(requestId, photonEvent.Sender);
                break;

            case CollectedEvent:
                if (photonEvent.CustomData is object[] collected && collected.Length >= 2
                    && collected[0] is int coinId && collected[1] is int collector)
                {
                    HandleCollected(coinId, collector);
                }
                break;
        }
    }

    void SpawnCoins(int firstId, Vector3 position, int[] values, int seed)
    {
        if (_coinPrefab == null || values == null) return;

        // 지급 장부는 바로 적는다 — 코인이 아직 다 솟지 않았어도 금액은 확정이다
        if (!PhotonNetwork.InRoom || PhotonNetwork.IsMasterClient)
        {
            float claimExpire = Time.time + coinLifetime + claimGrace;
            for (int i = 0; i < values.Length; i++)
                _claims[firstId + i] = new Claim { value = values[i], expireTime = claimExpire };
        }

        StartCoroutine(FountainRoutine(firstId, position, values.Length, seed));
    }

    // 한 닢씩 연달아 위로 쏘아 올린다 — 같은 시드로 굴려 모든 클라이언트에서 같은 모양으로 솟는다
    IEnumerator FountainRoutine(int firstId, Vector3 position, int count, int seed)
    {
        System.Random rng = new System.Random(seed);
        WaitForSeconds wait = count > 1 ? new WaitForSeconds(ejectInterval) : null;

        for (int i = 0; i < count; i++)
        {
            SpawnCoin(firstId + i, position, rng);
            if (i < count - 1) yield return wait;
        }
    }

    void SpawnCoin(int id, Vector3 position, System.Random rng)
    {
        float angle = (float)(rng.NextDouble() * Mathf.PI * 2f);
        float side = ejectSideSpeed * (0.3f + (float)rng.NextDouble() * 0.7f);
        float up = ejectUpSpeed * (0.85f + (float)rng.NextDouble() * 0.3f);
        Vector3 velocity = new Vector3(Mathf.Cos(angle) * side, up, Mathf.Sin(angle) * side);
        Vector3 spin = new Vector3(
            (float)(rng.NextDouble() * 2.0 - 1.0),
            (float)(rng.NextDouble() * 2.0 - 1.0),
            (float)(rng.NextDouble() * 2.0 - 1.0)) * ejectSpin;

        if (_coins.ContainsKey(id)) return;

        KillCoin coin = Instantiate(_coinPrefab, position, Quaternion.Euler(0f, angle * Mathf.Rad2Deg, 0f));
        coin.Init(this, _sparks, id, velocity, spin, coinLifetime);
        _coins[id] = coin;
    }

    void ConfirmPickup(int coinId, int collector)
    {
        if (!_claims.Remove(coinId, out Claim claim)) return;

        NetworkDataManager.Instance?.AddScrap(claim.value);
        PhotonNetwork.RaiseEvent(
            CollectedEvent,
            new object[] { coinId, collector },
            new RaiseEventOptions { Receivers = ReceiverGroup.All },
            SendOptions.SendReliable);
    }

    void HandleCollected(int coinId, int collector)
    {
        if (!_coins.Remove(coinId, out KillCoin coin) || coin == null) return;

        // 주운 본인은 이미 소리를 냈다. 몬스터가 삼킨 코인은 몬스터가 제 소리를 낸다
        if (collector != MonsterCollector
            && (!PhotonNetwork.InRoom || collector != PhotonNetwork.LocalPlayer.ActorNumber))
            PlayPickupSound(coin.transform.position);

        // 다른 사람이 먹은 코인은 그 사람 집게 쪽으로 빨려가는 모습을 보여 주고 사라진다
        if (TryFindCollectorAnchor(collector, out Transform anchor, out Vector3 anchorPosition))
            coin.FlyToCollector(anchor, anchorPosition);
        else
            coin.PlayCollected();
    }

    bool TryFindCollectorAnchor(int collector, out Transform anchor, out Vector3 position)
    {
        anchor = null;
        position = Vector3.zero;
        GameObject[] players = PlayerLookup.GetPlayers();
        for (int i = 0; i < players.Length; i++)
        {
            GameObject player = players[i];
            if (player == null || !player.activeInHierarchy) continue;

            PhotonView view = player.GetComponentInParent<PhotonView>();
            if (view == null || view.OwnerActorNr != collector) continue;

            ScrapMagnet magnet = view.GetComponentInChildren<ScrapMagnet>();
            anchor = magnet != null ? magnet.handAnchor : null;
            position = player.transform.position + Vector3.up * playerAnchorHeight;
            return true;
        }
        return false;
    }

    void SweepExpiredClaims()
    {
        if (_claims.Count == 0) return;

        _expiredScratch.Clear();
        foreach (KeyValuePair<int, Claim> pair in _claims)
            if (Time.time > pair.Value.expireTime)
                _expiredScratch.Add(pair.Key);

        for (int i = 0; i < _expiredScratch.Count; i++)
            _claims.Remove(_expiredScratch[i]);
    }

    void PlayPickupSound(Vector3 position)
    {
        if (_pickupClip == null) return;

        float pitch = 1f + Random.Range(-pickupPitchJitter, pickupPitchJitter);
        SoundPool.PlayClipAtPoint(_pickupClip, position, pickupVolume, pitch, 1f, pickupMaxDistance);
    }

    void FindLocalPlayer()
    {
        _localPlayer = null;
        _localStats = null;
        _localMagnet = null;

        GameObject[] players = PlayerLookup.GetPlayers();
        for (int i = 0; i < players.Length; i++)
        {
            GameObject player = players[i];
            if (player == null || !player.activeInHierarchy) continue;

            PhotonView view = player.GetComponentInParent<PhotonView>();
            if (PhotonNetwork.InRoom && (view == null || !view.IsMine)) continue;

            _localPlayer = player.transform;
            _localStats = player.GetComponentInParent<PlayerStats>();
            Transform root = view != null ? view.transform : player.transform;
            _localMagnet = root.GetComponentInChildren<ScrapMagnet>();
            return;
        }
    }
}
