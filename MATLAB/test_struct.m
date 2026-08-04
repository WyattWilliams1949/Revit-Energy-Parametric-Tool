function test_struct()
    try
        name = 'Walls: Generic';
        s = struct();
        s.(name) = 1;
        disp('Success!');
    catch ME
        disp(ME.message);
    end
end
