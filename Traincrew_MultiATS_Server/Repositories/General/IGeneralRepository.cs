﻿namespace Traincrew_MultiATS_Server.Repositories.General;

public interface IGeneralRepository
{
    Task Save<T>(T entity);
    Task SaveAll<T>(IEnumerable<T> entities);
    Task Delete<T>(T entity);
    Task DeleteAll<T>(IEnumerable<T> entities);
}