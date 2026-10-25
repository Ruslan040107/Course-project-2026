using System.Collections.Generic;
using UnityEngine;

public struct ShotInfo
{
    public Vector3 origin;
    public Vector3 end;
    public Vector3 normal;
    public bool hit;
    public bool headshot;
    public Health shooter;
    public Health target;   // the first player hit (a bullet can go on through him, see Weapon.FireThrough)
    public Transform muzzle;
    public float recoil;
    public bool melee;
    public bool stab;       // the knife's right click stab (melee too)
    public bool wallbang;   // the bullet went through at least one object before it hit
    // a shotgun pellet after the first one of the same shot: it has its own tracer, impact and blood,
    // but the sound, the flash, the recoil and shot counters use only the first pellet (pellet = false)
    public bool pellet;
    public bool taser;      // the Zeus x27 shot
    public bool leftHand;   // Dual Berettas: this shot came from the left pistol
    public bool silenced;   // USP-S, M4A1-S, MP5-SD: a quiet shot without a big flash
    public WeaponData weapon;   // the weapon that fired (null for a weapon without data)
}

public class Weapon : MonoBehaviour
{
    public static event System.Action<ShotInfo> AnyShot;
    public event System.Action ReloadStarted;
    // bullets go through objects (see Penetrable); RoundManager turns it off in simulations unless -penetration 1
    public static bool PenetrationEnabled = true;
    // a bullet went through an object: entry point, entry normal, exit point, exit normal (for bullet holes);
    // also for the second and next bodies a bullet hits (for blood, CombatEffects tells them by the point)
    public static event System.Action<Vector3, Vector3, Vector3, Vector3> AnyPenetration;

    // shots and the knife hurt teammates (FriendlyFireScale of the damage); RoundManager turns it off in simulations unless -ff 1
    public static bool FriendlyFire = true;
    // part of the damage a teammate takes (as bullets in CS 2)
    public const float FriendlyFireScale = 0.33f;

    // for sounds: any weapon started a reload / was taken in hands
    public static event System.Action<Weapon> AnyReloadStarted;
    public static event System.Action<Weapon> AnyEquipped;
    // shell-by-shell reload (Nova, XM1014, Sawed-Off): one shell went into the gun
    public static event System.Action<Weapon> AnyShellInserted;
    // the burst mode was switched on or off (Glock-18, FAMAS), for a HUD hint or a click sound
    public static event System.Action<Weapon> AnyModeSwitched;

    // solo test mode: the magazine never runs out
    public static bool InfiniteAmmo;

    PlayerController movement;

    [Header("Data (optional, overrides the values below)")]
    public WeaponData data;

    [Header("Info")]
    public string weaponName = "Rifle";

    [Header("Damage")]
    public Transform muzzle;
    public float damage = 25f;
    public float headMultiplier = 3f;
    public float legMultiplier = 0.75f;
    public float chestMultiplier = 1f;
    public float stomachMultiplier = 1.25f;
    [Tooltip("0..1, part of the damage that goes through armour")] public float armorPenetration = 0.775f;
    [Tooltip("full damage up to this distance, then it falls off (see FalloffAt); 0 = no falloff")]
    public float effectiveRange = 0f;
    [Tooltip("used when the hit collider has no HitZone: the head is the part of the collider above this height (0..1)")]
    [Range(0f, 1f)] public float headZoneStart = 0.72f;
    [Tooltip("the legs are the part of the collider below this height (0..1)")]
    [Range(0f, 1f)] public float legZoneEnd = 0.35f;
    [Tooltip("the stomach is the part of the collider between legZoneEnd and this height (0..1), the chest is above it")]
    [Range(0f, 1f)] public float stomachZoneEnd = 0.52f;

    [Header("Fire")]
    public float fireRate = 8f;
    public float range = 100f;
    public float spreadAngle = 0f;
    public bool automatic = true;
    public bool boltAction;
    public bool silenced;
    public bool dual;

    [Header("Burst mode (Glock-18, FAMAS)")]
    [Tooltip("shots in one burst, 0 or 1 = no burst mode")] public int burstCount;
    [Tooltip("shots per second inside a burst")] public float burstFireRate = 20f;
    [Tooltip("spread multiplier of the shots of a burst")] public float burstSpreadMultiplier = 1.25f;
    [Tooltip("pause after the last shot of a burst before the next burst, seconds")] public float burstCooldown = 0.35f;

    [Header("Shotgun")]
    [Tooltip("pellets in one shell, 0 or 1 = a single bullet; damage is per pellet")] public int pellets;
    [Tooltip("spread of the pellets around the aim point, degrees")] public float pelletSpread = 3f;
    [Tooltip("reloads one shell at a time")] public bool shellReload;
    public float shellReloadTime = 0.4f;
    [Tooltip("time before the first shell (set from reloadTime when there is data)")] public float shellStartDelay = 0.4f;

    [Header("Revolver (R8)")]
    [Tooltip("left click pulls the hammer for this time before the shot, 0 = shoots at once")] public float fireDelay;
    [Tooltip("spread multiplier of the shot after the hammer pull: it is very accurate")] public float hammerSpreadMultiplier = 0.5f;
    [Tooltip("right click fanning, shots per second; 0 = no alternative fire")] public float altFireRate;
    public float altSpreadMultiplier = 3f;

    [Header("Taser (Zeus)")]
    public bool taser;
    [Tooltip("seconds until the taser can fire again")] public float rechargeTime = 30f;

    [Header("Ammo")]
    public int magazineSize = 30;
    public int reserveAmmo = 90;
    public float reloadTime = 2.2f;

    [Header("Player accuracy (like CS 2; set from data, see WeaponData)")]
    [Tooltip("largest angle (degrees) of the first shot standing still / crouching")] public float standInaccuracy = 0.3f;
    public float crouchInaccuracy = 0.22f;
    [Tooltip("degrees added at full running speed")] public float moveInaccuracy = 5f;
    [Tooltip("degrees added by every shot, they fall to 10% in recoveryTime")] public float inaccuracyPerShot = 0.4f;
    public float recoveryTime = 0.4f;
    public RecoilPattern recoilPattern = RecoilPattern.M4;
    [Tooltip("how far up (degrees) the 10th bullet of a spray goes")] public float recoilScale = 3f;

    [Header("Other")]
    [Tooltip("bots: x spreadAngle (their old accuracy model, see BotSpread)")] public float botSpreadMultiplier = 2f;
    public float moveSpeedFactor = 1f;
    [Tooltip("speed factor while firing (last shot less than FiringTime ago), 0 = moveSpeedFactor")]
    public float firingMoveSpeedFactor;
    public float recoilFactor = 1f;

    // 0..1, how much the shooter moves; the player gets it from PlayerController, bots set it from BotController
    public float ExternalMoveInaccuracy { get; set; }

    // bots set these while they hop
    public bool ExternalInAir { get; set; }
    public float ExternalSpreadMultiplier { get; set; } = 1f;

    public bool IsMelee => data != null && data.melee;
    public Health Owner => owner;

    public bool IsTaser => taser;
    public bool IsShotgun => pellets > 1;
    public bool Silenced => silenced;
    public bool HasBurstMode => burstCount > 1;
    public bool HasAltFire => altFireRate > 0f;
    // a sniper scope with the black mask (AWP, SSG 08, SCAR-20, G3SG1)
    public bool HasSniperScope => data != null && data.HasScope && !data.scopeIsOptic;
    // a red dot / small optic (SG 553, AUG): zoom without the mask, the gun stays visible
    public bool HasOpticScope => data != null && data.HasScope && data.scopeIsOptic;
    // the bolt is cycled after every shot (AWP, SSG 08; from data.boltAction): the scope is left for it.
    // Auto-snipers (SCAR-20, G3SG1) have a sniper scope but no bolt
    public bool BoltAction => boltAction;

    // everything except automatic rifles reloads by itself when the magazine gets empty
    bool AutoReloads => !(data != null && data.category == WeaponCategory.Rifle && data.automatic);

    // the scope is in use: set by PlayerShoot for the player and by BotController for bots (scoped at a distance)
    public bool Scoped { get; set; } = true;

    // burst mode of the Glock-18 / FAMAS: the player switches it with the right mouse button, bots keep it off.
    // Each weapon keeps its own mode while the player switches between weapons.
    public bool BurstMode { get; set; }

    // "firing" for the move speed: the last shot is younger than this, seconds
    public const float FiringTime = 0.4f;

    // damage falloff: x FalloffPerRange for every effectiveRange behind effectiveRange, never below MinFalloff
    public const float FalloffPerRange = 0.85f;
    public const float MinFalloff = 0.5f;

    float nextShotTime;
    float drawEndTime;
    float reloadStart;
    float reloadEnd;
    float shellNext;          // shell reload: when the next shell goes in
    float lastShotTime = -10f;
    int ammo;
    int reserve;
    bool reloading;
    Health owner;

    int burstLeft;            // shots of the current burst still to fire
    float nextBurstShot;
    bool hammerPulling;       // R8: the hammer is being pulled, the shot comes at hammerShotTime
    float hammerShotTime;
    int dualShots;            // Dual Berettas: shots since the weapon was taken (odd = right pistol)

    readonly Dictionary<WeaponData, bool> burstModes = new Dictionary<WeaponData, bool>();

    public int Ammo { get { FinishReload(); return ammo; } }
    public int Reserve { get { FinishReload(); return reserve; } }
    public bool IsReloading { get { FinishReload(); return reloading; } }
    public float NextShotTime => nextShotTime;
    // the weapon is still being taken in the hands: no shots and no scope until the draw animation ends
    public bool IsDrawing => Time.time < drawEndTime;
    public float ReloadProgress => reloading ? Mathf.Clamp01((Time.time - reloadStart) / Mathf.Max(reloadEnd - reloadStart, 0.01f)) : 1f;

    // shell reload: progress 0..1 of the shell that goes in now (0 before the first one and when not reloading)
    public bool IsShellReload => shellReload;
    public float ShellProgress => reloading && shellReload
        ? Mathf.Clamp01(1f - (shellNext - Time.time) / Mathf.Max(shellReloadTime, 0.01f))
        : 0f;

    // a burst is being fired
    public bool IsBursting => burstLeft > 0;

    // R8: the hammer is being pulled (0..1), the shot comes when it reaches 1
    public bool IsHammerPulling => hammerPulling;

    // the player holds right click for R8 fanning (PlayerShoot sets it every frame; bots never do): the crosshair shows that spread
    [System.NonSerialized] public bool AltFireHeld;

    // R8: the left button was let go before the hammer was fully pulled, so there is no shot (like CS 2)
    public void CancelHammer()
    {
        hammerPulling = false;
    }
    public float HammerProgress => hammerPulling && fireDelay > 0f
        ? Mathf.Clamp01(1f - (hammerShotTime - Time.time) / fireDelay)
        : 0f;

    // Zeus: 1 = charged, then it grows from 0 to 1 during rechargeTime after a shot
    public float ChargeProgress
    {
        get
        {
            if (!taser) return 1f;
            FinishReload();
            if (ammo > 0) return 1f;
            return Mathf.Clamp01(1f - (nextShotTime - Time.time) / Mathf.Max(rechargeTime, 0.01f));
        }
    }

    // Dual Berettas: the last shot came from the left pistol (they take turns, the first shot is from the right one)
    public bool LeftHandShot => dual && dualShots > 0 && dualShots % 2 == 0;

    // shooting right now: a recent shot, a burst or a pulled hammer
    public bool IsFiring => Time.time - lastShotTime < FiringTime || hammerPulling || burstLeft > 0;

    // the player's speed factor: firingMoveSpeedFactor while firing (R8, G3SG1), otherwise moveSpeedFactor
    public float CurrentMoveSpeedFactor => firingMoveSpeedFactor > 0f && IsFiring ? firingMoveSpeedFactor : moveSpeedFactor;

    // the largest angle (degrees) the next shot can go off the aim, for the crosshair (the player: stance, movement and
    // the shots just fired, see PlayerInaccuracy); the burst spread and the pellet spread of shotguns are included
    public float CurrentSpread
    {
        get
        {
            float s = movement != null ? PlayerInaccuracy(out bool _) : BotSpread(out bool _);
            if (BurstMode && HasBurstMode) s *= burstSpreadMultiplier;
            if (AltFireHeld && HasAltFire) s *= altSpreadMultiplier;
            else if (fireDelay > 0f) s *= hammerSpreadMultiplier;
            if (IsShotgun) s += pelletSpread;
            return s;
        }
    }

    void Awake()
    {
        movement = GetComponentInParent<PlayerController>();
        owner = GetComponentInParent<Health>();
        if (data != null) CopyData();
        ResetAmmo();
    }

    void CopyData()
    {
        weaponName = data.weaponName;
        damage = data.damage;
        headMultiplier = data.headMultiplier;
        legMultiplier = data.legMultiplier;
        chestMultiplier = data.chestMultiplier;
        stomachMultiplier = data.stomachMultiplier;
        armorPenetration = data.armorPenetration;
        range = data.range;
        effectiveRange = data.effectiveRange;
        fireRate = data.fireRate;
        automatic = data.automatic;
        boltAction = data.boltAction;
        silenced = data.silenced;
        dual = data.dual;
        burstCount = data.burstCount;
        burstFireRate = data.burstFireRate;
        pellets = data.pellets;
        pelletSpread = data.pelletSpread;
        shellReload = data.shellReload;
        shellReloadTime = data.shellReloadTime;
        fireDelay = data.fireDelay;
        altFireRate = data.altFireRate;
        altSpreadMultiplier = data.altSpreadMultiplier;
        taser = data.taser;
        rechargeTime = data.rechargeTime;
        spreadAngle = data.spreadAngle;
        recoilFactor = data.recoil;
        standInaccuracy = data.standInaccuracy;
        crouchInaccuracy = data.crouchInaccuracy;
        moveInaccuracy = data.moveInaccuracy;
        inaccuracyPerShot = data.inaccuracyPerShot;
        recoveryTime = data.recoveryTime;
        recoilPattern = data.recoilPattern;
        recoilScale = data.recoilScale;
        magazineSize = data.magazineSize;
        reserveAmmo = data.reserveAmmo;
        reloadTime = data.reloadTime;
        moveSpeedFactor = data.moveSpeedFactor;
        firingMoveSpeedFactor = data.firingMoveSpeedFactor;

        // the whole shell reload (start + a full magazine of shells) takes about reloadTime, like in the weapon table
        // 0 when the shells alone already take the table's reload time (XM1014: 7 x 0.4 = 2.8)
        if (shellReload) shellStartDelay = Mathf.Clamp(reloadTime - magazineSize * shellReloadTime, 0f, 0.6f);
        // the taser holds one charge
        if (taser) magazineSize = Mathf.Max(1, magazineSize);
    }

    // ammoNow / reserveNow below 0 means a full magazine and full reserve
    public void Equip(WeaponData newData, int ammoNow = -1, int reserveNow = -1, float readyAt = 0f)
    {
        // every weapon remembers its burst mode
        if (data != null) burstModes[data] = BurstMode;

        data = newData;
        if (data != null) CopyData();

        ResetAmmo();
        if (ammoNow >= 0) ammo = Mathf.Min(ammoNow, magazineSize);
        if (reserveNow >= 0) reserve = reserveNow;
        // a used Zeus that charged again while it was put away comes back charged
        if (taser && ammo < magazineSize && readyAt <= Time.time) ammo = magazineSize;
        BurstMode = data != null && HasBurstMode && burstModes.TryGetValue(data, out bool burst) && burst;

        // the weapon is ready after its draw time, but never before its own shot cooldown has passed
        // (for the Zeus the cooldown is its recharge, so a used taser stays empty while it is put away)
        float drawEnd = Time.time + (data != null ? data.drawTime : 0f);
        nextShotTime = Mathf.Max(readyAt, drawEnd);
        drawEndTime = drawEnd;
        AnyEquipped?.Invoke(this);
    }

    public void ResetAmmo()
    {
        ammo = magazineSize;
        reserve = reserveAmmo;
        reloading = false;
        nextShotTime = 0f;
        lastShotTime = -10f;
        lastSlashTime = -10f;
        dualShots = 0;
        ResetRecoil();
        CancelTrigger();
    }

    // the player's spray starts again: no recoil and no inaccuracy from earlier shots (a new weapon, a new round)
    public void ResetRecoil()
    {
        bloomAtShot = 0f;
        punchAtShot = Vector2.zero;
        sprayAtShot = 0f;
    }

    public void ToggleBurstMode()
    {
        if (!HasBurstMode) return;
        BurstMode = !BurstMode;
        AnyModeSwitched?.Invoke(this);
    }

    void Update()
    {
        Tick();
    }

    void OnDisable()
    {
        CancelTrigger();
    }

    // stops a burst and a pulled hammer
    void CancelTrigger()
    {
        burstLeft = 0;
        hammerPulling = false;
    }

    // shells of a shell reload, the taser charge, the delayed R8 shot and the rest of a burst
    // (from Update, and from TryFire for bots that shoot in FixedUpdate)
    void Tick()
    {
        FinishReload();
        if (!hammerPulling && burstLeft <= 0) return;

        if ((owner != null && owner.IsDead) || IsDrawing || reloading || FireBlocked)
        {
            CancelTrigger();
            return;
        }

        if (hammerPulling && Time.time >= hammerShotTime)
        {
            hammerPulling = false;
            if (ammo > 0)
            {
                // the whole cycle (hammer + shot) takes 1 / fireRate, counted from the planned shot time
                // (a long frame in a fast simulation does not make the cycle longer)
                nextShotTime = hammerShotTime + Mathf.Max(0f, 1f / Mathf.Max(fireRate, 0.01f) - fireDelay);
                FireShot(hammerSpreadMultiplier);
            }
        }

        while (burstLeft > 0 && Time.time >= nextBurstShot)
        {
            if (ammo <= 0 || reloading)
            {
                burstLeft = 0;
                break;
            }
            burstLeft--;
            nextBurstShot += 1f / Mathf.Max(burstFireRate, 0.01f);
            FireShot(burstSpreadMultiplier);
        }
    }

    void FinishReload()
    {
        // the Zeus charges again by itself, it has no reload
        if (taser)
        {
            if (ammo < magazineSize && Time.time >= nextShotTime) ammo = magazineSize;
            return;
        }

        if (!reloading) return;

        if (shellReload)
        {
            while (reloading && Time.time >= shellNext) InsertShell();
            return;
        }

        if (Time.time < reloadEnd) return;

        int take = Mathf.Min(magazineSize - ammo, reserve);
        ammo += take;
        reserve -= take;
        reloading = false;
    }

    // shell reload: one shell from the reserve into the gun, the reload ends when the gun is full or the reserve is empty
    void InsertShell()
    {
        if (ammo < magazineSize && reserve > 0)
        {
            ammo++;
            reserve--;
            AnyShellInserted?.Invoke(this);
        }

        shellNext += shellReloadTime;
        if (ammo >= magazineSize || reserve <= 0) reloading = false;
    }

    public void StartReload()
    {
        FinishReload();
        if (taser) return;
        // infinite ammo (solo mode, console "ammo"): no reload, the magazine is simply full again
        // (a gun that was empty when it was turned on would wait for a reload forever)
        if (InfiniteAmmo)
        {
            if (!reloading && ammo < magazineSize) ammo = magazineSize;
            return;
        }
        if (IsDrawing) return;   // like shots and the scope: no reload while the weapon is being drawn
        if (reloading || ammo >= magazineSize || reserve <= 0) return;

        CancelTrigger();
        reloading = true;
        reloadStart = Time.time;

        if (shellReload)
        {
            // a short start, then one shell every shellReloadTime
            int shells = Mathf.Min(magazineSize - ammo, reserve);
            shellNext = Time.time + shellStartDelay + shellReloadTime;
            reloadEnd = Time.time + shellStartDelay + shells * shellReloadTime;
        }
        else
        {
            reloadEnd = Time.time + reloadTime;
        }

        ReloadStarted?.Invoke();
        AnyReloadStarted?.Invoke(this);
    }

    // the freeze time at the round start (nobody shoots, stabs or uses the Zeus; reloads go on),
    // and the frozen bots of the training option; both are never set in simulations
    bool FireBlocked => RoundManager.InFreezeTime || (RoundManager.BotsFrozen && movement == null && owner != null &&
                                                      owner.GetComponent<BotController>() != null);

    // left click (bots call it too): a shot, a burst in burst mode, or the R8 hammer pull
    public bool TryFire()
    {
        if (FireBlocked) return false;
        // nothing until the weapon is drawn (also after ResetAmmo, which clears the shot cooldown)
        if (IsDrawing) return false;
        if (IsMelee) return Melee(false);

        Tick();
        if (hammerPulling || burstLeft > 0) return false;

        // a shot stops a shell-by-shell reload if there is a shell in the gun (and the shot can go now)
        if (reloading && shellReload && ammo > 0 && Time.time >= nextShotTime) reloading = false;
        if (reloading) return false;

        if (ammo <= 0)
        {
            StartReload();
            return false;
        }

        if (Time.time < nextShotTime) return false;

        // R8: the hammer is pulled first, the shot comes fireDelay later (see Tick)
        if (fireDelay > 0f)
        {
            hammerPulling = true;
            hammerShotTime = Time.time + fireDelay;
            return false;
        }

        if (BurstMode && HasBurstMode)
        {
            // the first shot now, the others every 1 / burstFireRate (see Tick), then a pause
            float interval = 1f / Mathf.Max(burstFireRate, 0.01f);
            burstLeft = burstCount - 1;
            nextBurstShot = Time.time + interval;
            nextShotTime = Time.time + interval * (burstCount - 1) + Mathf.Max(burstCooldown, 1f / Mathf.Max(fireRate, 0.01f));
            FireShot(burstSpreadMultiplier);
            return true;
        }

        nextShotTime = Time.time + 1f / Mathf.Max(fireRate, 0.01f);
        FireShot(1f);
        return true;
    }

    // right click with the R8: fanning, fast inaccurate shots without the hammer delay
    public bool TryAltFire()
    {
        if (FireBlocked || !HasAltFire || IsMelee || IsDrawing) return false;

        Tick();
        if (burstLeft > 0) return false;

        if (reloading && shellReload && ammo > 0 && Time.time >= nextShotTime) reloading = false;
        if (reloading) return false;

        if (ammo <= 0)
        {
            StartReload();
            return false;
        }

        if (Time.time < nextShotTime) return false;

        hammerPulling = false;   // fanning instead of the pulled hammer
        nextShotTime = Time.time + 1f / altFireRate;
        FireShot(altSpreadMultiplier);
        return true;
    }

    // ---------------- accuracy ----------------
    // The player (like CS 2): the shot goes along the eye (PlayerController.AimRotation), moved by the aim punch of the
    // recoil pattern, plus a random direction inside the inaccuracy cone: a random angle around the aim and a random part
    // of the inaccuracy as the distance from it, so most shots land near the middle. Inaccuracy = stance (stand / crouch)
    // + movement + the shots just fired (they go away during recoveryTime). The pattern is the same every spray:
    // the first shot has no recoil, bullet N goes to point N of the pattern, the camera shows only part of it
    // (PlayerController.viewPunchScale), and after a short pause the punch goes back and the spray starts again.
    // Bots keep their old model (BotSpread): their accuracy did not change, so the simulations stay comparable.

    float bloomAtShot;        // degrees added by the shots, as it was right after the last shot
    Vector2 punchAtShot;      // aim punch right after the last shot (degrees: x up, y right)
    float sprayAtShot;        // spray position right after the last shot (shots)

    // the punch goes back like in CS 2: exponentially (1/s) plus linearly (degrees per second), so it really ends
    const float PunchReturnExp = 6f;
    const float PunchReturnLin = 6f;

    // the punch stays this long after a shot: a bit longer than the time between two shots of a spray (at most 0.15 s),
    // so a spray follows the pattern exactly
    float PunchHold => Mathf.Min(1f / Mathf.Max(fireRate, 0.01f), 0.15f) + 0.04f;

    // part of the last shot's punch left now: 1 during a spray, then down to 0
    float PunchLeft
    {
        get
        {
            float idle = Time.time - lastShotTime - PunchHold;
            if (idle <= 0f) return 1f;
            float m0 = punchAtShot.magnitude;
            if (m0 < 0.0001f) return 0f;
            const float c = PunchReturnLin / PunchReturnExp;
            float m = (m0 + c) * Mathf.Exp(-PunchReturnExp * idle) - c;
            return Mathf.Clamp01(m / m0);
        }
    }

    // the player's aim punch now (degrees: x up, y right): the next shot goes there, the camera shows part of it
    public Vector2 AimPunch => punchAtShot * PunchLeft;

    // the place of the next shot in the pattern (0 = the first shot of a spray, no recoil); goes back with the punch
    float SprayPosition
    {
        get
        {
            float s = sprayAtShot * PunchLeft;
            return s < 0.05f ? 0f : s;
        }
    }

    // the inaccuracy (degrees) left from the shots fired before: it falls to 10% in recoveryTime
    float ShotInaccuracy => bloomAtShot * Mathf.Pow(10f, -(Time.time - lastShotTime) / Mathf.Max(recoveryTime, 0.01f));

    // the player: the largest angle (degrees) the next shot can go off the aim (before the burst / R8 multipliers).
    // For a weapon with a scope (snipers, SG 553, AUG) stand/crouch are the scoped values,
    // without the scope they are x unscopedSpreadMultiplier (and at least unscopedMinSpread)
    float PlayerInaccuracy(out bool noScope)
    {
        bool scope = data != null && data.HasScope;
        bool unscoped = scope && !Scoped;
        // the no-scope kill (killfeed icon) is only for rifles with a sniper scope
        noScope = unscoped && HasSniperScope;

        float stance = Mathf.Lerp(crouchInaccuracy, standInaccuracy, movement.StandFactor);
        if (unscoped) stance = Mathf.Max(stance * data.unscopedSpreadMultiplier, data.unscopedMinSpread);

        // MoveInaccuracy: 0 standing still, 1 running, 1.5 in the air
        return stance + moveInaccuracy * movement.MoveInaccuracy + ShotInaccuracy;
    }

    // bots (unchanged): spreadAngle x botSpreadMultiplier x the bot's stance multiplier (crouch, hop) and the scope.
    // For a weapon with a scope spreadAngle is the scoped spread, without the scope it is x unscopedSpreadMultiplier
    // (and at least unscopedMinSpread). FireShot draws it in a square, as before.
    float BotSpread(out bool noScope)
    {
        float mult = botSpreadMultiplier * ExternalSpreadMultiplier;
        bool scope = data != null && data.HasScope;
        bool unscoped = scope && !Scoped;
        noScope = unscoped && HasSniperScope;
        if (unscoped) mult *= data.unscopedSpreadMultiplier;

        float spreadNow = spreadAngle * mult;
        if (unscoped) spreadNow = Mathf.Max(spreadNow, data.unscopedMinSpread);

        // scoped while moving: the scope does not help, the shot goes wide
        if (scope && Scoped) spreadNow = Mathf.Max(spreadNow, data.scopedMoveSpread * ExternalMoveInaccuracy);

        return spreadNow;
    }

    // ---------------- recoil patterns ----------------
    // Point i = where bullet i + 1 of a spray goes (up, right), in parts of recoilScale (the 10th bullet goes 1 up).
    // After the table the spray stays at the last height and swings between the last side offset and the opposite one.

    // AK-47, Galil AR, SG 553: a strong climb, then left, right and left again
    static readonly float[] AkUp = { 0f, 0.05f, 0.13f, 0.25f, 0.39f, 0.54f, 0.68f, 0.8f, 0.91f, 1f, 1.04f, 1.06f, 1.08f, 1.09f, 1.1f,
        1.11f, 1.12f, 1.13f, 1.14f, 1.15f, 1.16f, 1.17f, 1.18f, 1.19f, 1.2f, 1.21f, 1.22f, 1.23f, 1.24f, 1.25f };
    static readonly float[] AkRight = { 0f, 0f, 0.01f, 0.02f, 0.03f, 0.045f, 0.06f, 0.075f, 0.085f, 0.06f, -0.02f, -0.11f, -0.2f, -0.28f, -0.34f,
        -0.38f, -0.36f, -0.27f, -0.15f, -0.04f, 0.07f, 0.17f, 0.25f, 0.29f, 0.27f, 0.19f, 0.09f, 0f, -0.08f, -0.13f };
    // M4A4, M4A1-S, FAMAS, AUG: the same climb, then a small sway right and left
    static readonly float[] M4Up = { 0f, 0.05f, 0.13f, 0.25f, 0.39f, 0.54f, 0.68f, 0.8f, 0.91f, 1f, 1.03f, 1.05f, 1.06f, 1.07f, 1.08f,
        1.09f, 1.1f, 1.105f, 1.11f, 1.115f, 1.12f, 1.125f, 1.13f, 1.135f, 1.14f, 1.145f, 1.15f, 1.155f, 1.16f, 1.165f };
    static readonly float[] M4Right = { 0f, 0f, -0.01f, -0.01f, 0f, 0.02f, 0.04f, 0.07f, 0.1f, 0.13f, 0.16f, 0.18f, 0.18f, 0.15f, 0.1f,
        0.04f, -0.03f, -0.09f, -0.14f, -0.17f, -0.18f, -0.16f, -0.11f, -0.05f, 0.01f, 0.07f, 0.11f, 0.13f, 0.12f, 0.09f };
    // SMGs: a quicker, smaller climb and a small sway
    static readonly float[] SmgUp = { 0f, 0.08f, 0.2f, 0.34f, 0.49f, 0.63f, 0.76f, 0.86f, 0.94f, 1f, 1.03f, 1.05f, 1.07f, 1.08f, 1.09f,
        1.1f, 1.11f, 1.12f, 1.13f, 1.14f, 1.15f, 1.16f, 1.17f, 1.18f, 1.19f, 1.2f, 1.21f, 1.22f, 1.23f, 1.24f };
    static readonly float[] SmgRight = { 0f, 0.01f, 0.02f, 0.01f, -0.01f, -0.03f, -0.04f, -0.03f, 0f, 0.04f, 0.08f, 0.11f, 0.12f, 0.1f, 0.06f,
        0.01f, -0.05f, -0.1f, -0.13f, -0.13f, -0.1f, -0.05f, 0.01f, 0.07f, 0.11f, 0.12f, 0.1f, 0.05f, -0.01f, -0.06f };
    // machine guns: a long climb and a wide sway
    static readonly float[] MgUp = { 0f, 0.06f, 0.15f, 0.27f, 0.4f, 0.54f, 0.67f, 0.79f, 0.9f, 1f, 1.05f, 1.09f, 1.12f, 1.15f, 1.18f,
        1.2f, 1.22f, 1.24f, 1.26f, 1.28f, 1.29f, 1.3f, 1.31f, 1.32f, 1.33f, 1.34f, 1.35f, 1.36f, 1.37f, 1.38f };
    static readonly float[] MgRight = { 0f, 0.02f, 0.05f, 0.06f, 0.05f, 0.02f, -0.03f, -0.09f, -0.14f, -0.17f, -0.16f, -0.1f, -0.02f, 0.08f, 0.17f,
        0.23f, 0.25f, 0.21f, 0.12f, 0.01f, -0.11f, -0.21f, -0.27f, -0.27f, -0.2f, -0.09f, 0.04f, 0.16f, 0.24f, 0.27f };
    // pistols, shotguns and snipers: every shot kicks up (the first ones most), pistols wobble a little to the right
    static readonly float[] KickUp = { 0f, 0.17f, 0.32f, 0.45f, 0.57f, 0.68f, 0.77f, 0.86f, 0.93f, 1f, 1.06f, 1.11f, 1.16f, 1.2f, 1.24f,
        1.27f, 1.3f, 1.32f, 1.34f, 1.36f, 1.38f, 1.39f, 1.4f, 1.41f, 1.42f, 1.43f, 1.44f, 1.45f, 1.46f, 1.47f };
    static readonly float[] PistolRight = { 0f, 0.03f, 0.01f, 0.04f, 0.02f, 0.05f, 0.03f, 0.06f, 0.04f, 0.07f, 0.05f, 0.08f, 0.06f, 0.09f, 0.07f,
        0.1f, 0.08f, 0.11f, 0.09f, 0.12f, 0.1f, 0.13f, 0.11f, 0.14f, 0.12f, 0.15f, 0.13f, 0.16f, 0.14f, 0.17f };

    // the point of the pattern at spray position s (between two bullets it is in between), x = up, y = right
    public static Vector2 PatternPoint(RecoilPattern pattern, float s)
    {
        float[] up, right;
        switch (pattern)
        {
            case RecoilPattern.AK: up = AkUp; right = AkRight; break;
            case RecoilPattern.M4: up = M4Up; right = M4Right; break;
            case RecoilPattern.SMG: up = SmgUp; right = SmgRight; break;
            case RecoilPattern.MachineGun: up = MgUp; right = MgRight; break;
            case RecoilPattern.Pistol: up = KickUp; right = PistolRight; break;
            case RecoilPattern.Shotgun:
            case RecoilPattern.Sniper: up = KickUp; right = null; break;
            default: return Vector2.zero;
        }
        if (s <= 0f) return Vector2.zero;

        int last = up.Length - 1;
        if (s >= last)
        {
            // a long spray (machine guns, P90, Bizon, Galil): the last height, swinging sideways around the middle
            // (from the last side offset to the opposite one and back every 16 shots)
            float side = right != null ? right[last] * Mathf.Cos((s - last) * 2f * Mathf.PI / 16f) : 0f;
            return new Vector2(up[last], side);
        }

        int i = (int)s;
        float t = s - i;
        return new Vector2(Mathf.Lerp(up[i], up[i + 1], t), right != null ? Mathf.Lerp(right[i], right[i + 1], t) : 0f);
    }

    // one trigger pull: a round leaves the magazine, then one bullet, the pellets of a shell or the taser charge fly
    void FireShot(float spreadFactor)
    {
        // the player's inaccuracy, punch and spray position before this shot (they depend on the time of the last shot)
        bool player = movement != null;
        bool noScope = false;
        float playerSpread = 0f, shotsBefore = 0f, spray = 0f;
        Vector2 punch = Vector2.zero;
        if (player)
        {
            playerSpread = PlayerInaccuracy(out noScope) * spreadFactor;
            shotsBefore = ShotInaccuracy;
            punch = AimPunch;
            spray = SprayPosition;
        }

        ammo--;
        if (InfiniteAmmo) ammo = magazineSize;
        lastShotTime = Time.time;
        if (dual) dualShots++;
        if (ammo <= 0 && AutoReloads) StartReload();

        // the taser is empty until it charges again
        if (taser && !InfiniteAmmo) nextShotTime = Time.time + rechargeTime;

        Stealth st = GetComponentInParent<Stealth>();
        if (st != null) st.Reveal();

        bool inAir = player ? !movement.IsGrounded : ExternalInAir;

        Quaternion aim;
        if (player)
        {
            // a random direction around the aim, a random part of the inaccuracy as the angle off it
            float around = Random.Range(0f, 2f * Mathf.PI);
            float off = Random.value * playerSpread;
            Quaternion spread = Quaternion.Euler(Mathf.Sin(around) * off, Mathf.Cos(around) * off, 0f);
            aim = movement.AimRotation * Quaternion.Euler(-punch.x, punch.y, 0f) * spread;

            // this shot's recoil: more inaccuracy for the next shots, and the pattern goes on to the next bullet
            bloomAtShot = shotsBefore + inaccuracyPerShot;
            punchAtShot = punch + (PatternPoint(recoilPattern, spray + 1f) - PatternPoint(recoilPattern, spray)) * recoilScale;
            sprayAtShot = spray + 1f;
        }
        else
        {
            // bots: the old square spread around the muzzle (the same random numbers as before)
            float spreadNow = BotSpread(out noScope) * spreadFactor;
            Quaternion spread = Quaternion.Euler(
                Random.Range(-spreadNow, spreadNow),
                Random.Range(-spreadNow, spreadNow),
                0f);
            aim = muzzle.rotation * spread;
        }

        if (taser)
        {
            FireTaser(aim * Vector3.forward, inAir);
            return;
        }

        // a shotgun shell: the pellets scatter around the aim point, each one is a bullet of its own
        int count = Mathf.Max(1, pellets);
        for (int i = 0; i < count; i++)
        {
            Quaternion offset = Quaternion.identity;
            if (count > 1)
            {
                Vector2 p = Random.insideUnitCircle * pelletSpread;
                offset = Quaternion.Euler(p.y, p.x, 0f);
            }
            FireBullet(aim * offset * Vector3.forward, noScope, inAir, i > 0);
        }
    }

    // one bullet (or one pellet): damage, then the shot event for effects and sounds
    void FireBullet(Vector3 direction, bool noScope, bool inAir, bool pellet)
    {
        ShotInfo shot = new ShotInfo
        {
            origin = muzzle.position,
            end = muzzle.position + direction * range,
            shooter = owner,
            muzzle = muzzle,
            recoil = recoilFactor,
            pellet = pellet,
            leftHand = LeftHandShot,
            silenced = silenced,
            weapon = data
        };

        if (PenetrationEnabled)
        {
            FireThrough(direction, noScope, inAir, ref shot);
        }
        else if (Physics.Raycast(muzzle.position, direction, out RaycastHit hit, range))
        {
            // the old shot without penetration: the first collider stops the bullet,
            // a teammate takes damage only with friendly fire
            Health target = hit.collider.GetComponentInParent<Health>();
            bool friendly = owner != null && target != null && target.team == owner.team;
            bool head = false;

            if (target != null && target != owner && (!friendly || FriendlyFire))
            {
                float multiplier = ZoneMultiplier(hit, out head);
                if (friendly) multiplier *= FriendlyFireScale;
                target.TakeDamage(damage * multiplier * FalloffAt(hit.distance), owner, head, noScope, inAir, false, data);
            }

            shot.end = hit.point;
            shot.normal = hit.normal;
            shot.hit = true;
            shot.headshot = head;
            shot.target = target;

            Debug.DrawLine(muzzle.position, hit.point, Color.red, 0.1f);
        }

        AnyShot?.Invoke(shot);
    }

    // Zeus: a short ray, the first player in it takes the whole charge (no body parts, no penetration, no falloff)
    void FireTaser(Vector3 direction, bool inAir)
    {
        ShotInfo shot = new ShotInfo
        {
            origin = muzzle.position,
            end = muzzle.position + direction * range,
            shooter = owner,
            muzzle = muzzle,
            recoil = recoilFactor,
            taser = true,
            weapon = data
        };

        RaycastHit[] hits = Physics.RaycastAll(muzzle.position, direction, range,
                                               Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        for (int i = 0; i < hits.Length; i++)
        {
            RaycastHit hit = hits[i];
            if (hit.distance <= 0f) continue;

            Health target = hit.collider.GetComponentInParent<Health>();
            if (target != null && (target == owner || target.IsDead)) continue;

            bool friendly = owner != null && target != null && target.team == owner.team;
            if (target != null && (!friendly || FriendlyFire))
                target.TakeDamage(friendly ? damage * FriendlyFireScale : damage, owner, false, false, inAir, false, data);

            shot.end = hit.point;
            shot.normal = hit.normal;
            shot.hit = true;
            shot.target = target;
            break;
        }

        AnyShot?.Invoke(shot);
    }

    // part of the damage left at this distance: 1 up to effectiveRange, then x0.85 for every further effectiveRange,
    // never below half
    public float FalloffAt(float distance)
    {
        if (effectiveRange <= 0f || distance <= effectiveRange) return 1f;
        return Mathf.Max(MinFalloff, Mathf.Pow(FalloffPerRange, (distance - effectiveRange) / effectiveRange));
    }

    // damage multiplier of the body part that was hit
    float ZoneMultiplier(RaycastHit hit, out bool head)
    {
        head = false;
        HitZone hitZone = hit.collider.GetComponent<HitZone>();

        if (hitZone != null)
        {
            // character model with separate head, chest/arms, stomach and leg colliders
            if (hitZone.zone == BodyZone.Head)
            {
                head = true;
                return headMultiplier;
            }
            if (hitZone.zone == BodyZone.Legs) return legMultiplier;
            if (hitZone.zone == BodyZone.Stomach) return stomachMultiplier;
            return chestMultiplier;
        }

        // one collider: decide by the height of the hit
        Bounds b = hit.collider.bounds;
        float t = Mathf.InverseLerp(b.min.y, b.max.y, hit.point.y);

        if (t >= headZoneStart)
        {
            head = true;
            return headMultiplier;
        }
        if (t <= legZoneEnd) return legMultiplier;
        if (t <= stomachZoneEnd) return stomachMultiplier;
        return chestMultiplier;
    }

    // ---------------- penetration ----------------

    const float MaxThickness = 3f;      // a thicker object always stops the bullet
    const int MaxPenetrations = 4;      // the bullet goes through at most this many objects and bodies together
    const float MinDamageLeft = 0.1f;   // with less damage left the bullet gets stuck in the object

    // a body (player, bot, training target) works like an object of this strength (damage loss per metre),
    // its thickness along the shot is kept between these values
    const float BodyStrength = 1f;
    const float BodyMinThickness = 0.3f;
    const float BodyMaxThickness = 1f;

    // players hit by the current bullet: each one is hit once, even if the bullet crosses several of his colliders
    static readonly List<Health> hitBodies = new List<Health>();

    // the shot with penetration: the bullet goes through objects and bodies and loses damage in each of them,
    // the loss is strength * thickness / penetration power of the weapon (see Penetrable).
    // The shot info (for effects) describes the first player hit, or the point where the bullet stopped.
    void FireThrough(Vector3 direction, bool noScope, bool inAir, ref ShotInfo shot)
    {
        RaycastHit[] hits = Physics.RaycastAll(muzzle.position, direction, range,
                                               Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        float power = data != null ? data.penetration : 0f;
        float left = 1f;          // part of the damage the bullet still has
        int passed = 0;           // objects and bodies the bullet went through
        float behind = 0f;        // distance along the shot where the bullet left the last object or body
        bool hitPlayer = false;   // the shot info already describes the first player hit
        hitBodies.Clear();

        for (int i = 0; i < hits.Length; i++)
        {
            RaycastHit hit = hits[i];

            // the muzzle is inside this collider (a single Raycast does not see it either)
            if (hit.distance <= 0f) continue;

            Health target = hit.collider.GetComponentInParent<Health>();
            if (target != null)
            {
                // the shooter's own body, another collider of a player who was already hit, a dead player
                if (target == owner || target.IsDead || hitBodies.Contains(target)) continue;
                hitBodies.Add(target);

                bool friendly = owner != null && target.team == owner.team;

                // without friendly fire a teammate stops the bullet and takes no damage
                if (friendly && !FriendlyFire)
                {
                    if (!hitPlayer) StopShot(ref shot, hit.point, hit.normal, target, false, passed);
                    return;
                }

                // the second player and the next ones are hit through something, so it is a wallbang for them
                float multiplier = ZoneMultiplier(ZoneHit(hits, i, target), out bool head);
                if (friendly) multiplier *= FriendlyFireScale;
                target.TakeDamage(damage * multiplier * left * FalloffAt(hit.distance), owner, head, noScope, inAir, passed > 0, data);

                bool first = !hitPlayer;
                if (first)
                {
                    StopShot(ref shot, hit.point, hit.normal, target, head, passed);
                    hitPlayer = true;
                }

                // the bullet goes on through the body like through an object;
                // the damage loss uses the clamped thickness, the overlap with the next objects uses the real one
                float depth = BodyThickness(hit, direction);
                // with bone hitboxes the ray crosses several colliders of the same body (arm, chest, arm): the thickness
                // is from the entry into the first one to the exit from the last one (a capsule has only one collider)
                for (int j = i + 1; j < hits.Length; j++)
                {
                    if (hits[j].distance - hit.distance > BodyMaxThickness) break;
                    if (hits[j].collider.GetComponentInParent<Health>() != target) continue;
                    depth = Mathf.Max(depth, hits[j].distance - hit.distance + BodyThickness(hits[j], direction));
                }
                bool goesOn = power > 0f && passed < MaxPenetrations;
                if (goesOn)
                {
                    float bodyThickness = Mathf.Clamp(depth, BodyMinThickness, BodyMaxThickness);
                    left *= Mathf.Max(0f, 1f - BodyStrength * bodyThickness / power);
                    goesOn = left >= MinDamageLeft;
                }

                // blood on the second and next bodies, where the bullet goes in and out (AnyShot shows the first one)
                if (!first) AnyPenetration?.Invoke(hit.point, hit.normal, hit.point + direction * depth, direction);
                if (!goesOn) return;

                passed++;
                behind = Mathf.Max(behind, hit.distance + depth);
                continue;
            }

            // a wall, a box, the floor
            float strength = Penetrable.StrengthOf(hit.collider);
            bool canPass = power > 0f && !Penetrable.IsImpenetrable(strength) && passed < MaxPenetrations;

            // the exit: cast back at this collider from a point MaxThickness behind the entry;
            // if that point is still inside the object, there is no exit and the object is too thick
            Ray back = new Ray(hit.point + direction * MaxThickness, -direction);
            if (!canPass || !hit.collider.Raycast(back, out RaycastHit exit, MaxThickness + 0.01f) || exit.distance <= 0.001f)
            {
                if (!hitPlayer) StopShot(ref shot, hit.point, hit.normal, null, false, passed);
                return;
            }

            float exitDistance = hit.distance + Mathf.Max(0f, MaxThickness - exit.distance);

            // objects that overlap: only the part behind the previous object counts, an object fully inside it is skipped
            if (exitDistance <= behind) continue;
            float thickness = exitDistance - Mathf.Max(hit.distance, behind);

            left *= Mathf.Max(0f, 1f - strength * thickness / power);
            if (left < MinDamageLeft)
            {
                if (!hitPlayer) StopShot(ref shot, hit.point, hit.normal, null, false, passed);
                return;
            }

            passed++;
            behind = exitDistance;
            AnyPenetration?.Invoke(hit.point, hit.normal, exit.point, exit.normal);
        }

        // went through everything (or hit nothing) and flew on
        if (!hitPlayer) shot.wallbang = passed > 0;
    }

    // the collider that decides the body part: the first collider of this player along the shot that has a HitZone,
    // otherwise the collider the bullet went in through (then the height of the hit decides)
    static RaycastHit ZoneHit(RaycastHit[] hits, int first, Health target)
    {
        for (int i = first; i < hits.Length; i++)
        {
            if (hits[i].distance <= 0f || hits[i].collider.GetComponent<HitZone>() == null) continue;
            if (hits[i].collider.GetComponentInParent<Health>() == target) return hits[i];
        }
        return hits[first];
    }

    // how far the bullet goes inside a body collider: cast back at it from a point behind the entry, like for objects
    static float BodyThickness(RaycastHit hit, Vector3 direction)
    {
        const float reach = BodyMaxThickness * 2f;
        Ray back = new Ray(hit.point + direction * reach, -direction);
        if (hit.collider.Raycast(back, out RaycastHit exit, reach + 0.01f) && exit.distance > 0.001f)
            return reach - exit.distance;
        return BodyMaxThickness;
    }

    // the bullet stopped at this point (in an object or in a player)
    static void StopShot(ref ShotInfo shot, Vector3 point, Vector3 normal, Health target, bool head, int passed)
    {
        shot.end = point;
        shot.normal = normal;
        shot.hit = true;
        shot.headshot = head;
        shot.target = target;
        shot.wallbang = passed > 0;

        Debug.DrawLine(shot.origin, point, Color.red, 0.1f);
    }

    // right click with the knife
    public bool TrySecondary()
    {
        return !FireBlocked && IsMelee && !IsDrawing && Melee(true);
    }

    bool Melee(bool stab)
    {
        if (Time.time < nextShotTime) return false;
        nextShotTime = Time.time + (stab ? data.secondaryInterval : 1f / Mathf.Max(fireRate, 0.01f));
        lastShotTime = Time.time;

        Stealth st = GetComponentInParent<Stealth>();
        if (st != null) st.Reveal();

        bool inAir = movement != null ? !movement.IsGrounded : ExternalInAir;
        Vector3 origin = muzzle.position;
        Vector3 dir = muzzle.forward;

        ShotInfo shot = new ShotInfo
        {
            origin = origin,
            end = origin + dir * data.meleeRange,
            shooter = owner,
            muzzle = muzzle,
            melee = true,
            stab = stab,
            weapon = data
        };

        // a slash soon after the previous slash is weaker (CS: 40 the first one, then 25)
        bool followUp = !stab && data.followUpDamage > 0f && Time.time - lastSlashTime < FollowUpTime;
        if (!stab) lastSlashTime = Time.time;

        if (MeleeHit(origin, dir, data.meleeRange, out RaycastHit hit))
        {
            Health target = hit.collider.GetComponentInParent<Health>();
            bool friendly = owner != null && target != null && target.team == owner.team;

            // a teammate gets hurt only with friendly fire
            if (target != null && owner != null && target != owner && (!friendly || FriendlyFire) && !target.IsDead)
            {
                float dmg = stab ? data.secondaryDamage : followUp ? data.followUpDamage : damage;

                Vector3 toTarget = target.transform.position - owner.transform.position;
                toTarget.y = 0f;
                if (toTarget.sqrMagnitude > 0.01f && Vector3.Dot(target.transform.forward, toTarget.normalized) > 0.5f)
                    dmg = stab ? data.backstabDamage : data.backstabDamage * 0.5f;

                if (friendly) dmg *= FriendlyFireScale;
                target.TakeDamage(dmg, owner, false, false, inAir, false, data);
            }

            shot.end = hit.point;
            shot.normal = hit.normal;
            shot.hit = true;
            shot.target = target;
        }

        AnyShot?.Invoke(shot);
        return true;
    }

    // the knife: a slash this soon after the previous slash does data.followUpDamage
    const float FollowUpTime = 0.8f;
    float lastSlashTime = -10f;
    static readonly RaycastHit[] meleeHits = new RaycastHit[16];

    // the nearest thing in front of the knife, like one SphereCast, but the attacker's own colliders
    // (the hitboxes of his model: arms, legs) never stop the blade
    bool MeleeHit(Vector3 origin, Vector3 dir, float length, out RaycastHit best)
    {
        best = default;
        int n = Physics.SphereCastNonAlloc(origin, 0.2f, dir, meleeHits, length);
        bool found = false;
        for (int i = 0; i < n; i++)
        {
            RaycastHit h = meleeHits[i];
            // distance 0: the sphere starts inside this collider (a single SphereCast skips it too)
            if (h.distance <= 0f || (found && h.distance >= best.distance)) continue;
            if (owner != null && h.collider.GetComponentInParent<Health>() == owner) continue;
            best = h;
            found = true;
        }
        return found;
    }
}
