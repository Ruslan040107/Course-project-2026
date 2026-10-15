using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(NavMeshAgent), typeof(Health))]
public class BotController : MonoBehaviour
{
    public Weapon weapon;
    public Transform head;
    public float viewRange = 40f;
    public float stopDistance = 12f;
    public float reactionTime = 0.4f;
    public float turnSpeed = 8f;

    NavMeshAgent agent;
    Health self;
    Health target;
    float seenTime;
    float retargetTimer;

    void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        self = GetComponent<Health>();
        agent.updateRotation = false;
    }
    void OnEnable()
    {
        target = null;
        seenTime = 0f;
    }
    void FixedUpdate()
    {
        retargetTimer -= Time.fixedDeltaTime;
        if (target == null || target.IsDead || !target.gameObject.activeInHierarchy || retargetTimer <= 0f)
        {
            target = FindClosestEnemy();
            retargetTimer = 1f;
        }

        if (target == null)
        {
            agent.ResetPath();
            return;
        }

        float distance = Vector3.Distance(transform.position, target.transform.position);

        if (CanSee(target))
        {
            seenTime += Time.fixedDeltaTime;
            Face(target.transform.position - transform.position);

            if (distance > stopDistance) agent.SetDestination(target.transform.position);
            else agent.ResetPath();

            if (seenTime >= reactionTime)
            {
                head.LookAt(target.transform.position);
                weapon.TryFire();
            }
        }
        else
        {
            seenTime = 0f;
            agent.SetDestination(target.transform.position);
            Face(agent.desiredVelocity);
        }
    }

    void Face(Vector3 direction)
    {
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.01f) return;

        Quaternion look = Quaternion.LookRotation(direction);
        transform.rotation = Quaternion.Slerp(transform.rotation, look, turnSpeed * Time.fixedDeltaTime);
    }

    bool CanSee(Health t)
    {
        Vector3 from = head.position;
        Vector3 toTarget = t.transform.position - from;

        if (toTarget.magnitude > viewRange) return false;

        if (Physics.Raycast(from, toTarget.normalized, out RaycastHit hit, viewRange))
            return hit.collider.GetComponentInParent<Health>() == t;

        return false;
    }

    Health FindClosestEnemy()
    {
        Health best = null;
        float bestDistance = float.MaxValue;

        foreach (Health h in Health.All)
        {
            if (h == self || h.team == self.team || h.IsDead) continue;

            float d = (h.transform.position - transform.position).sqrMagnitude;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = h;
            }
        }
        return best;
    }
}