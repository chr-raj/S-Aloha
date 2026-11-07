import { getUser } from '../../api'
import useManagerOn from '../../core/useManagerOn'

/**
 * PRJ-04 : chef de projet (responsable, compte utilisateur) ou gestionnaire du périmètre du
 * projet, comme `ProjectRights.IsLead` de l'API (PER-06 : le rôle par périmètre prime sur le
 * rôle AD).
 */
export default function useIsLead(project) {
  const manager = useManagerOn(project.perimeterId)
  const u = getUser()
  if (manager) return true
  return project.ownerType !== 'Group' && !!project.ownerId
    && project.ownerId.toLowerCase() === (u?.username ?? '').toLowerCase()
}
