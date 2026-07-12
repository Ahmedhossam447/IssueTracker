using System.Collections.Generic;
using System.Threading.Tasks;

namespace IssueTracker.Core.Interfaces;

public interface IGenericRepository<T> where T : class
{
    Task<T?> GetByIdAsync(Guid id);
    Task<IEnumerable<T>> GetAllAsync();
    Task<(IEnumerable<T> Items, int TotalCount)> GetPagedAsync(
        int pageNumber, 
        int pageSize, 
        System.Func<System.Linq.IQueryable<T>, System.Linq.IOrderedQueryable<T>>? orderBy = null);
    Task AddAsync(T entity);
    Task UpdateAsync(T entity);
    Task DeleteAsync(T entity);
}
